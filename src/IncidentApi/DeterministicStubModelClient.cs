using AzureAgenticOps.AgentRuntime;
using AzureAgenticOps.Contracts;
using AzureAgenticOps.RuleEvaluator;
using AzureAgenticOps.Safety;

namespace AzureAgenticOps.IncidentApi;

/// <summary>
/// A deterministic stand-in for a remote language model, used until the remote
/// model integration milestone. The client synthesizes valid structured agent
/// output from the deterministic rule catalog and the action policy, so the
/// complete workflow can run locally without any model endpoint. Because the
/// output is derived from policy-governed inputs it can never propose an action
/// outside the allow-list.
/// </summary>
public sealed class DeterministicStubModelClient : IAgentModelClient
{
    private const string ModelId = "stub-deterministic";

    private readonly IncidentRuleEvaluator _ruleEvaluator;
    private readonly ActionPolicyEvaluator _policyEvaluator;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new deterministic stub model client.</summary>
    /// <param name="ruleEvaluator">The deterministic rule evaluator.</param>
    /// <param name="policyEvaluator">The deterministic action policy evaluator.</param>
    /// <param name="timeProvider">The time provider. Defaults to <see cref="TimeProvider.System"/>.</param>
    public DeterministicStubModelClient(
        IncidentRuleEvaluator ruleEvaluator,
        ActionPolicyEvaluator policyEvaluator,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(ruleEvaluator);
        ArgumentNullException.ThrowIfNull(policyEvaluator);
        _ruleEvaluator = ruleEvaluator;
        _policyEvaluator = policyEvaluator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<AgentModelResponse<T>> GenerateStructuredResponseAsync<T>(
        AgentModelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset startedAt = _timeProvider.GetUtcNow();

        object value;
        if (typeof(T) == typeof(InvestigationResult))
        {
            AgentPayload payload = ContractSerialization.Deserialize<AgentPayload>(request.UserInput);
            value = BuildInvestigationResult(payload);
        }
        else if (typeof(T) == typeof(RemediationPlan))
        {
            AgentPayload payload = ContractSerialization.Deserialize<AgentPayload>(request.UserInput);
            value = BuildRemediationPlan(payload);
        }
        else
        {
            throw new ModelResponseValidationException(
                $"The deterministic stub model client does not support response type '{typeof(T).Name}'.");
        }

        var metadata = new ModelInvocationMetadata(
            request.PromptName,
            request.PromptVersion,
            request.ModelId ?? ModelId,
            _timeProvider.GetUtcNow() - startedAt,
            new ModelUsage(request.UserInput.Length / 4, OutputTokens: null),
            ValidationSucceeded: true,
            RetryCount: 0);

        return Task.FromResult(new AgentModelResponse<T>((T)value, metadata));
    }

    private InvestigationResult BuildInvestigationResult(AgentPayload payload)
    {
        Incident incident = payload.Incident;
        IReadOnlyList<IncidentEvidence> evidence = payload.Evidence ?? [];
        RuleEvaluationResult rules = _ruleEvaluator.Evaluate(incident, evidence);

        var observations = evidence
            .Select(item => $"{item.EvidenceType} evidence '{item.EvidenceId}' from {item.Source}.")
            .ToList();

        RemediationAction? proposedAction = null;
        if (rules.RecommendedDisposition == AgentDisposition.Resolve && rules.ProposedActionType is not null)
        {
            RemediationAction candidate = DemoRemediationActionBuilder.Build(incident, rules.ProposedActionType, "tier1", rules.MaxActionAttempts);
            ActionPolicyDecision decision = _policyEvaluator.Evaluate(candidate);

            // The fast path may only carry actions that policy allows without
            // human approval; anything else is escalated to Tier 2 planning.
            if (decision.IsAllowed && !decision.RequiresApproval)
            {
                proposedAction = candidate;
            }
        }

        bool resolve = proposedAction is not null;
        var hypotheses = new List<AgentHypothesis>
        {
            new(
                rules.MatchedPatternName is not null
                    ? $"The incident matches the known pattern '{rules.MatchedPatternName}'."
                    : "The incident does not match any known operational pattern.",
                rules.Confidence,
                rules.MatchedEvidenceIds),
        };

        return new InvestigationResult(
            SchemaVersions.V1,
            incident.IncidentId,
            rules.Classification,
            $"Tier 1 review of '{incident.Title}' based on {evidence.Count} evidence item(s).",
            observations,
            hypotheses,
            rules.Confidence,
            resolve ? AgentDisposition.Resolve : AgentDisposition.Escalate,
            proposedAction,
            MissingEvidence: [],
            ReasoningSummary: rules.ReasonSummary +
                (resolve
                    ? " An approved deterministic action is available for automatic execution."
                    : " No automatically executable deterministic action is available; escalating to Tier 2."));
    }

    private RemediationPlan BuildRemediationPlan(AgentPayload payload)
    {
        Incident incident = payload.Incident;
        IReadOnlyList<IncidentEvidence> evidence = payload.Evidence ?? [];
        RuleEvaluationResult rules = _ruleEvaluator.Evaluate(incident, evidence);

        string actionType = rules.ProposedActionType ?? ActionTypeCatalog.RestartDemoWorkload;
        RemediationAction action = DemoRemediationActionBuilder.Build(incident, actionType, "tier2", Math.Max(1, rules.MaxActionAttempts));
        ActionPolicyDecision decision = _policyEvaluator.Evaluate(action);

        AgentHypothesis rootCause = payload.Tier1Handoff?.Hypotheses.Count > 0
            ? payload.Tier1Handoff.Hypotheses[0]
            : new AgentHypothesis(
                "The most likely cause derived from rule evaluation.",
                rules.Confidence,
                rules.MatchedEvidenceIds);

        var verification = new List<VerificationStep>
        {
            new(
                "ResourceStatus",
                VerificationTarget(incident),
                ExpectedValue: "healthy"),
        };

        return new RemediationPlan(
            SchemaVersions.V1,
            incident.IncidentId,
            $"Tier 2 remediation plan for '{incident.Title}' using the predefined action '{actionType}'.",
            rootCause,
            decision.RiskLevel,
            decision.RequiresApproval,
            Actions: [action],
            Verification: verification,
            Rollback: [],
            ReasoningSummary: rules.ReasonSummary +
                " The plan uses only predefined action types and defers the final risk decision to policy.");
    }

    /// <summary>Builds the verification target identifier for an incident's primary service.</summary>
    /// <param name="incident">The incident under remediation.</param>
    /// <returns>The deterministic verification target.</returns>
    public static string VerificationTarget(Incident incident)
    {
        ArgumentNullException.ThrowIfNull(incident);
        return $"demo/deployment/{DemoRemediationActionBuilder.PrimaryService(incident)}";
    }

    /// <summary>
    /// The subset of the agent user input consumed by the stub. Unknown JSON
    /// properties in the payload are ignored.
    /// </summary>
    /// <param name="Incident">The incident under investigation.</param>
    /// <param name="Evidence">The evidence supplied to the agent.</param>
    /// <param name="Tier1Handoff">The Tier 1 handoff, present for Tier 2 requests.</param>
    private sealed record AgentPayload(
        Incident Incident,
        IReadOnlyList<IncidentEvidence>? Evidence,
        InvestigationResult? Tier1Handoff);
}
