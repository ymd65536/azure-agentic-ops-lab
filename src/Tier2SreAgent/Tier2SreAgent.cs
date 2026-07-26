using AzureAgenticOps.AgentRuntime;
using AzureAgenticOps.Contracts;
using AzureAgenticOps.Safety;

namespace AzureAgenticOps.Tier2SreAgent;

/// <summary>
/// Options controlling Tier 2 remediation planning behavior.
/// </summary>
/// <param name="MaxModelAttempts">
/// The maximum number of model invocations per planning request, including one
/// bounded repair attempt after invalid structured output.
/// </param>
/// <param name="AllowAutomaticLowRiskExecution">
/// Whether validated low-risk plans may proceed without human approval. This
/// must only be enabled in explicitly configured demo environments.
/// </param>
public sealed record Tier2AgentOptions(
    int MaxModelAttempts = 2,
    bool AllowAutomaticLowRiskExecution = true)
{
    /// <summary>Gets the default Tier 2 options.</summary>
    public static Tier2AgentOptions Default { get; } = new();
}

/// <summary>
/// The outcome of Tier 2 remediation planning, containing the validated plan
/// after deterministic risk normalization and the model invocation metadata.
/// </summary>
/// <param name="Plan">The plan after deterministic guards. Risk levels reflect policy, not the model.</param>
/// <param name="ModelMetadata">The metadata of the final successful model invocation.</param>
public sealed record Tier2PlanningOutcome(
    RemediationPlan Plan,
    ModelInvocationMetadata ModelMetadata);

/// <summary>
/// The Tier 2 SRE agent: the deep reasoning path. The agent reviews the complete
/// structured Tier 1 handoff and evidence, then asks the model for a structured
/// remediation plan. Deterministic code enforces the risk floor: the plan risk
/// level can never be lower than the fixed catalog classification of its actions,
/// unknown action types invalidate the plan, and medium- or high-risk plans
/// always require human approval regardless of model output.
/// </summary>
public sealed class Tier2SreAgent
{
    private const string PromptName = "tier2-remediation";
    private const string PromptVersion = "1.1";

    private readonly IAgentModelClient _modelClient;
    private readonly IPromptStore _promptStore;
    private readonly Tier2AgentOptions _options;

    /// <summary>Initializes a new Tier 2 agent.</summary>
    /// <param name="modelClient">The model client used for non-deterministic reasoning.</param>
    /// <param name="promptStore">The version-controlled prompt store.</param>
    /// <param name="options">The Tier 2 options. Defaults to <see cref="Tier2AgentOptions.Default"/>.</param>
    public Tier2SreAgent(
        IAgentModelClient modelClient,
        IPromptStore promptStore,
        Tier2AgentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modelClient);
        ArgumentNullException.ThrowIfNull(promptStore);
        _modelClient = modelClient;
        _promptStore = promptStore;
        _options = options ?? Tier2AgentOptions.Default;
    }

    /// <summary>
    /// Produces a validated remediation plan from the Tier 1 handoff and evidence.
    /// </summary>
    /// <param name="incident">The incident under investigation.</param>
    /// <param name="tier1Handoff">The complete structured Tier 1 investigation result.</param>
    /// <param name="evidence">The evidence collected for the incident.</param>
    /// <param name="correlationId">The correlation identifier for observability.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The planning outcome.</returns>
    /// <exception cref="ModelResponseValidationException">
    /// The model produced invalid structured output on every bounded attempt.
    /// </exception>
    public async Task<Tier2PlanningOutcome> PlanAsync(
        Incident incident,
        InvestigationResult tier1Handoff,
        IReadOnlyList<IncidentEvidence> evidence,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(tier1Handoff);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        PromptDefinition prompt = _promptStore.Load(PromptName, PromptVersion);

        string userInput = ContractSerialization.Serialize(new
        {
            incident,
            tier1Handoff,
            evidence,
            allowedActionTypes = ActionTypeCatalog.All,
        });

        var request = new AgentModelRequest(
            prompt.Name,
            prompt.Version,
            prompt.Content,
            userInput,
            CorrelationId: correlationId);

        AgentModelResponse<RemediationPlan>? response = null;
        ModelResponseValidationException? lastValidationFailure = null;

        for (int attempt = 1; attempt <= _options.MaxModelAttempts; attempt++)
        {
            try
            {
                AgentModelResponse<RemediationPlan> candidate =
                    await _modelClient.GenerateStructuredResponseAsync<RemediationPlan>(request, cancellationToken)
                        .ConfigureAwait(false);

                ValidateStructure(candidate.Value, incident.IncidentId);
                response = candidate;
                break;
            }
            catch (ModelResponseValidationException exception)
            {
                lastValidationFailure = exception;
            }
        }

        if (response is null)
        {
            throw new ModelResponseValidationException(
                $"Tier 2 model output for incident '{incident.IncidentId}' was invalid after {_options.MaxModelAttempts} attempt(s).",
                lastValidationFailure!);
        }

        RemediationPlan guarded = ApplyRiskFloor(response.Value);
        return new Tier2PlanningOutcome(guarded, response.Metadata);
    }

    private static void ValidateStructure(RemediationPlan plan, string expectedIncidentId)
    {
        var failures = new List<string>();

        if (!string.Equals(plan.SchemaVersion, SchemaVersions.V1, StringComparison.Ordinal))
        {
            failures.Add($"Unexpected schema version '{plan.SchemaVersion}'.");
        }

        if (!string.Equals(plan.IncidentId, expectedIncidentId, StringComparison.Ordinal))
        {
            failures.Add($"Plan incident ID '{plan.IncidentId}' does not match '{expectedIncidentId}'.");
        }

        if (plan.RootCauseHypothesis is null)
        {
            failures.Add("Root cause hypothesis must be present.");
        }
        else if (plan.RootCauseHypothesis.Confidence is < 0.0 or > 1.0)
        {
            failures.Add($"Root cause confidence {plan.RootCauseHypothesis.Confidence} is outside [0.0, 1.0].");
        }

        if (string.IsNullOrWhiteSpace(plan.Summary))
        {
            failures.Add("Summary must not be empty.");
        }

        foreach (RemediationAction action in plan.Actions ?? [])
        {
            if (!ActionTypeCatalog.IsKnown(action.ActionType))
            {
                failures.Add($"Action type '{action.ActionType}' is not on the allow-list.");
            }
        }

        if (failures.Count > 0)
        {
            throw new ModelResponseValidationException(
                "Tier 2 structured output failed validation: " + string.Join(' ', failures));
        }
    }

    /// <summary>
    /// Normalizes the plan risk level and approval requirement. The authoritative
    /// risk level is the maximum fixed catalog classification across all plan
    /// actions; the model can raise but never lower it. Medium- and high-risk
    /// plans always require approval, and low-risk plans require approval unless
    /// automatic low-risk execution is explicitly enabled.
    /// </summary>
    private RemediationPlan ApplyRiskFloor(RemediationPlan plan)
    {
        RiskLevel floor = RiskLevel.Low;
        foreach (RemediationAction action in plan.Actions)
        {
            if (ActionTypeCatalog.TryGet(action.ActionType, out ActionTypeDefinition? definition) &&
                definition!.RiskLevel > floor)
            {
                floor = definition.RiskLevel;
            }
        }

        RiskLevel authoritative = plan.RiskLevel > floor ? plan.RiskLevel : floor;

        bool requiresApproval = authoritative switch
        {
            RiskLevel.Low => plan.RequiresApproval || !_options.AllowAutomaticLowRiskExecution,
            _ => true,
        };

        return plan with
        {
            RiskLevel = authoritative,
            RequiresApproval = requiresApproval,
        };
    }
}
