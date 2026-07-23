using AzureAgenticOps.AgentRuntime;
using AzureAgenticOps.Contracts;
using AzureAgenticOps.Safety;

namespace AzureAgenticOps.Tier1SreAgent;

/// <summary>
/// Options controlling Tier 1 investigation behavior.
/// </summary>
/// <param name="ConfidenceThreshold">
/// The minimum confidence required for a Tier 1 result to recommend resolution.
/// Results below the threshold are deterministically escalated to Tier 2.
/// </param>
/// <param name="MaxModelAttempts">
/// The maximum number of model invocations per investigation, including one
/// bounded repair attempt after invalid structured output.
/// </param>
public sealed record Tier1AgentOptions(
    double ConfidenceThreshold = 0.8,
    int MaxModelAttempts = 2)
{
    /// <summary>Gets the default Tier 1 options.</summary>
    public static Tier1AgentOptions Default { get; } = new();
}

/// <summary>
/// The outcome of a Tier 1 investigation, containing the validated structured
/// result and the model invocation metadata for observability.
/// </summary>
/// <param name="Result">The validated investigation result after deterministic guards.</param>
/// <param name="Insights">The Insights retrieval result supplied to the model.</param>
/// <param name="ModelMetadata">The metadata of the final successful model invocation.</param>
public sealed record Tier1InvestigationOutcome(
    InvestigationResult Result,
    InsightsResult Insights,
    ModelInvocationMetadata ModelMetadata);

/// <summary>
/// The Tier 1 SRE agent: the fast investigation path. The agent reviews incident
/// evidence, searches the Insights knowledge base, and asks the model for a
/// structured investigation result. Deterministic code, not the model, enforces
/// the escalation threshold, strips disallowed proposed actions, and validates
/// the structured output before it is passed downstream.
/// </summary>
public sealed class Tier1SreAgent
{
    private const string PromptName = "tier1-investigation";
    private const string PromptVersion = "1.0";

    private readonly IAgentModelClient _modelClient;
    private readonly IPromptStore _promptStore;
    private readonly InsightsCapability _insights;
    private readonly Tier1AgentOptions _options;

    /// <summary>Initializes a new Tier 1 agent.</summary>
    /// <param name="modelClient">The model client used for non-deterministic reasoning.</param>
    /// <param name="promptStore">The version-controlled prompt store.</param>
    /// <param name="insights">The Insights retrieval capability.</param>
    /// <param name="options">The Tier 1 options. Defaults to <see cref="Tier1AgentOptions.Default"/>.</param>
    public Tier1SreAgent(
        IAgentModelClient modelClient,
        IPromptStore promptStore,
        InsightsCapability insights,
        Tier1AgentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modelClient);
        ArgumentNullException.ThrowIfNull(promptStore);
        ArgumentNullException.ThrowIfNull(insights);
        _modelClient = modelClient;
        _promptStore = promptStore;
        _insights = insights;
        _options = options ?? Tier1AgentOptions.Default;
    }

    /// <summary>
    /// Investigates an incident and produces a validated structured result.
    /// </summary>
    /// <param name="incident">The incident under investigation.</param>
    /// <param name="evidence">The evidence collected for the incident.</param>
    /// <param name="correlationId">The correlation identifier for observability.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The investigation outcome.</returns>
    /// <exception cref="ModelResponseValidationException">
    /// The model produced invalid structured output on every bounded attempt.
    /// </exception>
    public async Task<Tier1InvestigationOutcome> InvestigateAsync(
        Incident incident,
        IReadOnlyList<IncidentEvidence> evidence,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        InsightsResult insights = _insights.Search(incident, evidence);
        PromptDefinition prompt = _promptStore.Load(PromptName, PromptVersion);

        string userInput = ContractSerialization.Serialize(new
        {
            incident,
            evidence,
            insights,
            allowedActionTypes = ActionTypeCatalog.All,
        });

        var request = new AgentModelRequest(
            prompt.Name,
            prompt.Version,
            prompt.Content,
            userInput,
            CorrelationId: correlationId);

        AgentModelResponse<InvestigationResult>? response = null;
        ModelResponseValidationException? lastValidationFailure = null;

        for (int attempt = 1; attempt <= _options.MaxModelAttempts; attempt++)
        {
            try
            {
                AgentModelResponse<InvestigationResult> candidate =
                    await _modelClient.GenerateStructuredResponseAsync<InvestigationResult>(request, cancellationToken)
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
                $"Tier 1 model output for incident '{incident.IncidentId}' was invalid after {_options.MaxModelAttempts} attempt(s).",
                lastValidationFailure!);
        }

        InvestigationResult guarded = ApplyDeterministicGuards(response.Value);
        return new Tier1InvestigationOutcome(guarded, insights, response.Metadata);
    }

    private static void ValidateStructure(InvestigationResult result, string expectedIncidentId)
    {
        var failures = new List<string>();

        if (!string.Equals(result.SchemaVersion, SchemaVersions.V1, StringComparison.Ordinal))
        {
            failures.Add($"Unexpected schema version '{result.SchemaVersion}'.");
        }

        if (!string.Equals(result.IncidentId, expectedIncidentId, StringComparison.Ordinal))
        {
            failures.Add($"Result incident ID '{result.IncidentId}' does not match '{expectedIncidentId}'.");
        }

        if (result.Confidence is < 0.0 or > 1.0)
        {
            failures.Add($"Confidence {result.Confidence} is outside [0.0, 1.0].");
        }

        if (string.IsNullOrWhiteSpace(result.Summary))
        {
            failures.Add("Summary must not be empty.");
        }

        if (failures.Count > 0)
        {
            throw new ModelResponseValidationException(
                "Tier 1 structured output failed validation: " + string.Join(' ', failures));
        }
    }

    /// <summary>
    /// Applies deterministic guards to the model output. Policy code, not the
    /// model, decides whether the result may recommend resolution:
    /// low-confidence results are escalated and proposed actions that are not on
    /// the allow-list are removed.
    /// </summary>
    private InvestigationResult ApplyDeterministicGuards(InvestigationResult result)
    {
        InvestigationResult guarded = result;

        if (guarded.ProposedAction is not null && !ActionTypeCatalog.IsKnown(guarded.ProposedAction.ActionType))
        {
            guarded = guarded with
            {
                ProposedAction = null,
                RecommendedDisposition = AgentDisposition.Escalate,
                ReasoningSummary = guarded.ReasoningSummary +
                    " [guard] Proposed action type was not on the allow-list and was removed; escalating.",
            };
        }

        if (guarded.RecommendedDisposition == AgentDisposition.Resolve &&
            guarded.Confidence < _options.ConfidenceThreshold)
        {
            guarded = guarded with
            {
                RecommendedDisposition = AgentDisposition.Escalate,
                ProposedAction = null,
                ReasoningSummary = guarded.ReasoningSummary +
                    $" [guard] Confidence {guarded.Confidence:0.00} is below the threshold {_options.ConfidenceThreshold:0.00}; escalating.",
            };
        }

        if (guarded.RecommendedDisposition == AgentDisposition.Resolve && guarded.ProposedAction is null)
        {
            guarded = guarded with
            {
                RecommendedDisposition = AgentDisposition.Escalate,
                ReasoningSummary = guarded.ReasoningSummary +
                    " [guard] Resolution was recommended without an approved deterministic action; escalating.",
            };
        }

        return guarded;
    }
}
