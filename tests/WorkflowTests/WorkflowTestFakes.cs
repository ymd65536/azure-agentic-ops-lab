using AzureAgenticOps.Contracts;
using AzureAgenticOps.IncidentWorkflow;
using AzureAgenticOps.RuleEvaluator;

namespace AzureAgenticOps.WorkflowTests;

/// <summary>
/// Deterministic fake activities for workflow tests. Each activity can be
/// scripted with results or failures per attempt.
/// </summary>
internal sealed class FakeWorkflowActivities : IIncidentWorkflowActivities
{
    public Queue<Func<IReadOnlyList<IncidentEvidence>>> EvidenceResults { get; } = new();

    public Func<RuleEvaluationResult>? RuleResult { get; set; }

    public Queue<Func<InvestigationResult>> Tier1Results { get; } = new();

    public Queue<Func<RemediationPlan>> Tier2Results { get; } = new();

    public Queue<Func<RemediationAction, ExecutionResult>> ExecutionResults { get; } = new();

    public Queue<Func<VerificationResult>> VerificationResults { get; } = new();

    public List<(RemediationAction Action, bool ApprovalGranted)> ExecutedActions { get; } = [];

    public int Tier1Invocations { get; private set; }

    public int Tier2Invocations { get; private set; }

    public Task<IReadOnlyList<IncidentEvidence>> CollectEvidenceAsync(
        Incident incident, int attemptNumber, string correlationId, CancellationToken cancellationToken)
    {
        if (EvidenceResults.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<IncidentEvidence>>([]);
        }

        return Task.FromResult(EvidenceResults.Dequeue()());
    }

    public Task<RuleEvaluationResult> EvaluateRulesAsync(
        Incident incident, IReadOnlyList<IncidentEvidence> evidence, string correlationId, CancellationToken cancellationToken)
    {
        RuleEvaluationResult result = RuleResult?.Invoke() ?? new RuleEvaluationResult(
            IncidentClassification.Unknown,
            MatchedPatternName: null,
            MatchedEvidenceIds: [],
            Confidence: 0.0,
            AgentDisposition.Escalate,
            EscalateToTier2: true,
            ProposedActionType: null,
            MaxActionAttempts: 0,
            "No rule matched.");
        return Task.FromResult(result);
    }

    public Task<InvestigationResult> RunTier1InvestigationAsync(
        Incident incident, IReadOnlyList<IncidentEvidence> evidence, string correlationId, CancellationToken cancellationToken)
    {
        Tier1Invocations++;
        return Task.FromResult(Tier1Results.Dequeue()());
    }

    public Task<RemediationPlan> RunTier2PlanningAsync(
        Incident incident, InvestigationResult tier1Handoff, IReadOnlyList<IncidentEvidence> evidence, string correlationId, CancellationToken cancellationToken)
    {
        Tier2Invocations++;
        return Task.FromResult(Tier2Results.Dequeue()());
    }

    public Task<ExecutionResult> ExecuteActionAsync(
        Incident incident, RemediationAction action, bool approvalGranted, string correlationId, CancellationToken cancellationToken)
    {
        ExecutedActions.Add((action, approvalGranted));
        if (ExecutionResults.Count == 0)
        {
            return Task.FromResult(WorkflowTestData.Execution(incident.IncidentId, action, ExecutionOutcome.Succeeded));
        }

        return Task.FromResult(ExecutionResults.Dequeue()(action));
    }

    public Task<VerificationResult> VerifyTier1RemediationAsync(
        Incident incident, RemediationAction executedAction, string correlationId, CancellationToken cancellationToken) =>
        Task.FromResult(VerificationResults.Dequeue()());

    public Task<VerificationResult> VerifyPlanAsync(
        Incident incident, RemediationPlan plan, string correlationId, CancellationToken cancellationToken) =>
        Task.FromResult(VerificationResults.Dequeue()());
}

/// <summary>
/// A scripted approval gate that returns queued decisions.
/// </summary>
internal sealed class FakeApprovalGate : IApprovalGate
{
    public Queue<ApprovalDecision> Decisions { get; } = new();

    public List<TimeSpan> ObservedTimeouts { get; } = [];

    public Task<ApprovalDecision> WaitForApprovalAsync(
        Incident incident, RemediationPlan plan, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObservedTimeouts.Add(timeout);
        return Task.FromResult(Decisions.Count > 0
            ? Decisions.Dequeue()
            : new ApprovalDecision(ApprovalOutcome.TimedOut));
    }
}

/// <summary>
/// Shared test data builders for workflow tests.
/// </summary>
internal static class WorkflowTestData
{
    public static Incident Incident(string incidentId = "inc-001") => new(
        SchemaVersions.V1,
        incidentId,
        "Demo incident",
        "A demo incident used by workflow tests.",
        "monitor",
        "sev2",
        ["demo-service"],
        DateTimeOffset.UnixEpoch);

    public static IncidentEvidence Evidence(string evidenceId = "ev-001", string incidentId = "inc-001") => new(
        SchemaVersions.V1,
        evidenceId,
        incidentId,
        "log",
        "demo-service",
        "connection refused to upstream gateway",
        DateTimeOffset.UnixEpoch);

    public static RemediationAction Action(string actionType = "restart_deployment", string key = "inc-001-restart-1") => new(
        actionType,
        new ActionTarget("demo", "deployment", "demo-service"),
        new Dictionary<string, string>(),
        key);

    public static InvestigationResult Tier1Result(
        AgentDisposition disposition,
        double confidence = 0.9,
        RemediationAction? proposedAction = null,
        string incidentId = "inc-001") => new(
        SchemaVersions.V1,
        incidentId,
        IncidentClassification.Known,
        "Tier 1 summary",
        ["observation"],
        [new AgentHypothesis("hypothesis", confidence, ["ev-001"])],
        confidence,
        disposition,
        proposedAction,
        [],
        "Tier 1 reasoning summary");

    public static RemediationPlan Plan(
        bool requiresApproval,
        IReadOnlyList<RemediationAction>? actions = null,
        IReadOnlyList<RemediationAction>? rollback = null,
        string incidentId = "inc-001") => new(
        SchemaVersions.V1,
        incidentId,
        "Tier 2 plan summary",
        new AgentHypothesis("root cause", 0.85, ["ev-001"]),
        requiresApproval ? RiskLevel.Medium : RiskLevel.Low,
        requiresApproval,
        actions ?? [Action("rollback_deployment", "inc-001-plan-1")],
        [new VerificationStep("HttpStatus", "demo-service", "200")],
        rollback ?? [],
        "Tier 2 reasoning summary");

    public static ExecutionResult Execution(
        string incidentId,
        RemediationAction action,
        ExecutionOutcome outcome,
        string message = "mock execution") => new(
        SchemaVersions.V1,
        incidentId,
        action.ActionType,
        action.IdempotencyKey,
        outcome,
        message,
        AttemptNumber: 1,
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch);

    public static VerificationResult Verification(VerificationOutcome outcome, string incidentId = "inc-001") => new(
        SchemaVersions.V1,
        incidentId,
        outcome,
        [new VerificationCheckResult("HttpStatus", "demo-service", "200", outcome == VerificationOutcome.Passed ? "200" : "404", outcome == VerificationOutcome.Passed)],
        DateTimeOffset.UnixEpoch);
}
