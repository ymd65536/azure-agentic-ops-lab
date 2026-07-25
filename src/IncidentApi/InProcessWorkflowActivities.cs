using AzureAgenticOps.Contracts;
using AzureAgenticOps.ExecutionService;
using AzureAgenticOps.IncidentWorkflow;
using AzureAgenticOps.RuleEvaluator;
using AzureAgenticOps.Tier1SreAgent;
using AzureAgenticOps.Tier2SreAgent;
using AzureAgenticOps.VerificationService;

namespace AzureAgenticOps.IncidentApi;

/// <summary>
/// Workflow activities wired directly to the library implementations, hosted in
/// the same process. Each member keeps the same service boundary as the future
/// Dapr service-invocation implementation, so swapping the transport does not
/// change the orchestrator.
/// </summary>
public sealed class InProcessWorkflowActivities : IIncidentWorkflowActivities
{
    private readonly InMemoryEvidenceStore _evidenceStore;
    private readonly IncidentRuleEvaluator _ruleEvaluator;
    private readonly Tier1SreAgent.Tier1SreAgent _tier1Agent;
    private readonly Tier2SreAgent.Tier2SreAgent _tier2Agent;
    private readonly MockExecutionService _executionService;
    private readonly VerificationEvaluator _verificationEvaluator;

    /// <summary>Initializes the in-process activities.</summary>
    /// <param name="evidenceStore">The store holding submitted evidence.</param>
    /// <param name="ruleEvaluator">The deterministic rule evaluator.</param>
    /// <param name="tier1Agent">The Tier 1 SRE agent.</param>
    /// <param name="tier2Agent">The Tier 2 SRE agent.</param>
    /// <param name="executionService">The mock execution service.</param>
    /// <param name="verificationEvaluator">The verification evaluator.</param>
    public InProcessWorkflowActivities(
        InMemoryEvidenceStore evidenceStore,
        IncidentRuleEvaluator ruleEvaluator,
        Tier1SreAgent.Tier1SreAgent tier1Agent,
        Tier2SreAgent.Tier2SreAgent tier2Agent,
        MockExecutionService executionService,
        VerificationEvaluator verificationEvaluator)
    {
        ArgumentNullException.ThrowIfNull(evidenceStore);
        ArgumentNullException.ThrowIfNull(ruleEvaluator);
        ArgumentNullException.ThrowIfNull(tier1Agent);
        ArgumentNullException.ThrowIfNull(tier2Agent);
        ArgumentNullException.ThrowIfNull(executionService);
        ArgumentNullException.ThrowIfNull(verificationEvaluator);
        _evidenceStore = evidenceStore;
        _ruleEvaluator = ruleEvaluator;
        _tier1Agent = tier1Agent;
        _tier2Agent = tier2Agent;
        _executionService = executionService;
        _verificationEvaluator = verificationEvaluator;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IncidentEvidence>> CollectEvidenceAsync(
        Incident incident,
        int attemptNumber,
        string correlationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_evidenceStore.Get(incident.IncidentId));
    }

    /// <inheritdoc />
    public Task<RuleEvaluationResult> EvaluateRulesAsync(
        Incident incident,
        IReadOnlyList<IncidentEvidence> evidence,
        string correlationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_ruleEvaluator.Evaluate(incident, evidence));
    }

    /// <inheritdoc />
    public async Task<InvestigationResult> RunTier1InvestigationAsync(
        Incident incident,
        IReadOnlyList<IncidentEvidence> evidence,
        string correlationId,
        CancellationToken cancellationToken)
    {
        Tier1InvestigationOutcome outcome = await _tier1Agent
            .InvestigateAsync(incident, evidence, correlationId, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Result;
    }

    /// <inheritdoc />
    public async Task<RemediationPlan> RunTier2PlanningAsync(
        Incident incident,
        InvestigationResult tier1Handoff,
        IReadOnlyList<IncidentEvidence> evidence,
        string correlationId,
        CancellationToken cancellationToken)
    {
        Tier2PlanningOutcome outcome = await _tier2Agent
            .PlanAsync(incident, tier1Handoff, evidence, correlationId, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Plan;
    }

    /// <inheritdoc />
    public Task<ExecutionResult> ExecuteActionAsync(
        Incident incident,
        RemediationAction action,
        bool approvalGranted,
        string correlationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExecutionResult result = _executionService.Execute(
            new ExecutionRequest(incident.IncidentId, action, approvalGranted, correlationId));
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<VerificationResult> VerifyTier1RemediationAsync(
        Incident incident,
        RemediationAction executedAction,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var steps = new List<VerificationStep>
        {
            new(
                "ResourceStatus",
                DeterministicStubModelClient.VerificationTarget(incident),
                ExpectedValue: "healthy"),
        };
        return _verificationEvaluator.VerifyAsync(incident.IncidentId, steps, cancellationToken);
    }

    /// <inheritdoc />
    public Task<VerificationResult> VerifyPlanAsync(
        Incident incident,
        RemediationPlan plan,
        string correlationId,
        CancellationToken cancellationToken) =>
        _verificationEvaluator.VerifyAsync(incident.IncidentId, plan.Verification, cancellationToken);
}
