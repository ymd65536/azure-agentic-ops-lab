using AzureAgenticOps.Contracts;
using AzureAgenticOps.IncidentWorkflow;

namespace AzureAgenticOps.WorkflowTests;

public sealed class IncidentWorkflowOrchestratorTests
{
    private readonly FakeWorkflowActivities _activities = new();
    private readonly FakeApprovalGate _approvalGate = new();
    private readonly InMemoryLifecycleEventPublisher _publisher = new();

    private IncidentWorkflowOrchestrator CreateOrchestrator(IncidentWorkflowOptions? options = null) =>
        new(_activities, _approvalGate, _publisher, options);

    private Task<IncidentWorkflowResult> RunAsync(IncidentWorkflowOptions? options = null) =>
        CreateOrchestrator(options).RunAsync(
            WorkflowTestData.Incident(), "wf-001", "corr-001", CancellationToken.None);

    /// <summary>
    /// Options for the legacy demo behavior in which Tier 1 remediates directly
    /// and a plan that policy considers approval-free executes automatically.
    /// </summary>
    private static readonly IncidentWorkflowOptions AutonomousDemoOptions = IncidentWorkflowOptions.Default with
    {
        Tier1PlansRequireTier2RiskAssessment = false,
        Tier2PlansAlwaysRequireApproval = false,
    };

    [Fact]
    public async Task Tier1FastPath_WhenRiskAssessmentIsDisabled_ResolvesIncident()
    {
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.Tier1Results.Enqueue(() =>
            WorkflowTestData.Tier1Result(AgentDisposition.Resolve, 0.95, WorkflowTestData.Action()));
        _activities.VerificationResults.Enqueue(() => WorkflowTestData.Verification(VerificationOutcome.Passed));

        IncidentWorkflowResult result = await RunAsync(AutonomousDemoOptions);

        Assert.Equal(IncidentWorkflowState.Resolved, result.FinalState);
        Assert.Equal(0, _activities.Tier2Invocations);
        Assert.Equal(
            [
                IncidentWorkflowState.Received,
                IncidentWorkflowState.Classifying,
                IncidentWorkflowState.RuleEvaluation,
                IncidentWorkflowState.Tier1Investigation,
                IncidentWorkflowState.Executing,
                IncidentWorkflowState.Verifying,
                IncidentWorkflowState.Resolved,
            ],
            result.StateHistory);
    }

    [Fact]
    public async Task RuleFastPath_ResolvesWithoutAnyModelCall()
    {
        RemediationAction ruleAction = WorkflowTestData.Action("restart_demo_workload", "inc-001-rule-1");
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.RuleResult = () => WorkflowTestData.KnownRuleResult();
        _activities.RuleRemediationResult = () => new RuleRemediationDecision(
            CanAutoExecute: true, ruleAction, "Policy allows automatic execution of the low-risk action.");
        _activities.VerificationResults.Enqueue(() => WorkflowTestData.Verification(VerificationOutcome.Passed));

        IncidentWorkflowResult result = await RunAsync();

        Assert.Equal(IncidentWorkflowState.Resolved, result.FinalState);
        Assert.Equal(0, _activities.Tier1Invocations);
        Assert.Equal(0, _activities.Tier2Invocations);
        Assert.Equal(1, _activities.RuleRemediationPreparations);
        Assert.False(_activities.ExecutedActions.Single().ApprovalGranted);
        Assert.Equal(
            [
                IncidentWorkflowState.Received,
                IncidentWorkflowState.Classifying,
                IncidentWorkflowState.RuleEvaluation,
                IncidentWorkflowState.Executing,
                IncidentWorkflowState.Verifying,
                IncidentWorkflowState.Resolved,
            ],
            result.StateHistory);
    }

    [Fact]
    public async Task RuleFastPath_WhenPolicyDeclinesAutoExecution_InvestigatesWithTier1()
    {
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.RuleResult = () => WorkflowTestData.KnownRuleResult();
        _activities.RuleRemediationResult = () => new RuleRemediationDecision(
            CanAutoExecute: false, Action: null, "The action requires human approval.");
        _activities.Tier1Results.Enqueue(() =>
            WorkflowTestData.Tier1Result(AgentDisposition.Resolve, 0.95, WorkflowTestData.Action()));
        _activities.Tier2Results.Enqueue(() => WorkflowTestData.Plan(requiresApproval: true));
        _approvalGate.Decisions.Enqueue(new ApprovalDecision(ApprovalOutcome.Approved, "oncall", "Reviewed"));
        _activities.VerificationResults.Enqueue(() => WorkflowTestData.Verification(VerificationOutcome.Passed));

        IncidentWorkflowResult result = await RunAsync();

        Assert.Equal(IncidentWorkflowState.Resolved, result.FinalState);
        Assert.Equal(1, _activities.RuleRemediationPreparations);
        Assert.Equal(1, _activities.Tier1Invocations);
        Assert.Contains(IncidentWorkflowState.Tier1Investigation, result.StateHistory);
        RuleHandlingSummary handoff = Assert.Single(_activities.Tier1RuleHandoffs);
        Assert.False(handoff.AutoExecutionAllowed);
        Assert.Contains("approval", handoff.EscalationReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RuleFastPath_WhenVerificationFails_EscalatesToTier1WithoutRetryingTheRuleAction()
    {
        RemediationAction ruleAction = WorkflowTestData.Action("restart_demo_workload", "inc-001-rule-1");
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.RuleResult = () => WorkflowTestData.KnownRuleResult();
        _activities.RuleRemediationResult = () => new RuleRemediationDecision(
            CanAutoExecute: true, ruleAction, "Policy allows automatic execution of the low-risk action.");
        _activities.VerificationResults.Enqueue(() => WorkflowTestData.Verification(VerificationOutcome.Failed));
        _activities.Tier1Results.Enqueue(() => WorkflowTestData.Tier1Result(AgentDisposition.Escalate, 0.4));
        _activities.Tier2Results.Enqueue(() => WorkflowTestData.Plan(requiresApproval: false));
        _approvalGate.Decisions.Enqueue(new ApprovalDecision(ApprovalOutcome.Approved, "oncall", "Reviewed"));
        _activities.VerificationResults.Enqueue(() => WorkflowTestData.Verification(VerificationOutcome.Passed));

        IncidentWorkflowResult result = await RunAsync();

        Assert.Equal(IncidentWorkflowState.Resolved, result.FinalState);
        Assert.Equal(1, _activities.RuleRemediationPreparations);
        Assert.Equal(1, _activities.Tier1Invocations);
        Assert.Equal(1, _activities.Tier2Invocations);
        Assert.Single(_activities.ExecutedActions, executed => executed.Action.IdempotencyKey == ruleAction.IdempotencyKey);
        RuleHandlingSummary handoff = Assert.Single(_activities.Tier1RuleHandoffs);
        Assert.True(handoff.AutoExecutionAllowed);
        Assert.Equal(ExecutionOutcome.Succeeded, handoff.ExecutionOutcome);
        Assert.Equal(VerificationOutcome.Failed, handoff.VerificationOutcome);
    }

    [Fact]
    public async Task RuleFastPath_IsSkippedWhenTheRuleEscalates()
    {
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.RuleResult = () => WorkflowTestData.KnownRuleResult() with
        {
            RecommendedDisposition = AgentDisposition.Escalate,
            EscalateToTier2 = true,
            ProposedActionType = null,
        };
        _activities.Tier1Results.Enqueue(() => WorkflowTestData.Tier1Result(AgentDisposition.Escalate, 0.4));
        _activities.Tier2Results.Enqueue(() => WorkflowTestData.Plan(requiresApproval: false));
        _approvalGate.Decisions.Enqueue(new ApprovalDecision(ApprovalOutcome.Approved, "oncall", "Reviewed"));
        _activities.VerificationResults.Enqueue(() => WorkflowTestData.Verification(VerificationOutcome.Passed));

        IncidentWorkflowResult result = await RunAsync();

        Assert.Equal(IncidentWorkflowState.Resolved, result.FinalState);
        Assert.Equal(0, _activities.RuleRemediationPreparations);
        Assert.Equal(1, _activities.Tier1Invocations);
    }

    [Fact]
    public async Task Tier1Escalation_InvokesTier2_AndApprovedPlanResolves()
    {
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.Tier1Results.Enqueue(() => WorkflowTestData.Tier1Result(AgentDisposition.Escalate, 0.4));
        _activities.Tier2Results.Enqueue(() => WorkflowTestData.Plan(requiresApproval: true));
        _approvalGate.Decisions.Enqueue(new ApprovalDecision(ApprovalOutcome.Approved, "oncall", "Looks safe"));
        _activities.VerificationResults.Enqueue(() => WorkflowTestData.Verification(VerificationOutcome.Passed));

        IncidentWorkflowResult result = await RunAsync();

        Assert.Equal(IncidentWorkflowState.Resolved, result.FinalState);
        Assert.Equal(1, _activities.Tier2Invocations);
        Assert.Contains(IncidentWorkflowState.AwaitingApproval, result.StateHistory);
        Assert.True(_activities.ExecutedActions.Single().ApprovalGranted);
    }

    [Fact]
    public async Task Tier1Plan_IsSharedWithTier2ForRiskAssessment_AndAsksForHumanApproval()
    {
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.Tier1Results.Enqueue(() =>
            WorkflowTestData.Tier1Result(AgentDisposition.Resolve, 0.95, WorkflowTestData.Action()));
        _activities.Tier2Results.Enqueue(() => WorkflowTestData.Plan(requiresApproval: false));
        _approvalGate.Decisions.Enqueue(new ApprovalDecision(ApprovalOutcome.Approved, "oncall", "Reviewed"));
        _activities.VerificationResults.Enqueue(() => WorkflowTestData.Verification(VerificationOutcome.Passed));

        IncidentWorkflowResult result = await RunAsync();

        Assert.Equal(IncidentWorkflowState.Resolved, result.FinalState);
        Assert.Equal(1, _activities.Tier2Invocations);
        Assert.Contains(IncidentWorkflowState.AwaitingApproval, result.StateHistory);
        Assert.True(_activities.ExecutedActions.Single().ApprovalGranted);

        Assert.Contains(_publisher.Events, item => item.EventType == "Tier1RuleHandoffShared");
        Assert.Contains(_publisher.Events, item => item.EventType == "Tier1RemediationPlanProposed");
        Assert.Contains(_publisher.Events, item => item.EventType == "Tier1PlanSharedWithTier2");
        Assert.Contains(_publisher.Events, item => item.EventType == "Tier2RiskAssessmentShared");
        Assert.Contains(_publisher.Events, item => item.EventType == "ApprovalRequested");
    }

    [Fact]
    public async Task Tier1RuleHandoff_SummarizesTheRuleBasedHandling()
    {
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.RuleResult = () => WorkflowTestData.KnownRuleResult();
        _activities.RuleRemediationResult = () => new RuleRemediationDecision(
            CanAutoExecute: false, Action: null, "The action requires human approval.");
        _activities.Tier1Results.Enqueue(() => WorkflowTestData.Tier1Result(AgentDisposition.Escalate, 0.4));
        _activities.Tier2Results.Enqueue(() => WorkflowTestData.Plan(requiresApproval: true));
        _approvalGate.Decisions.Enqueue(new ApprovalDecision(ApprovalOutcome.Rejected, "oncall", "Too risky"));

        await RunAsync();

        RuleHandlingSummary handoff = Assert.Single(_activities.Tier1RuleHandoffs);
        Assert.Equal("inc-001", handoff.IncidentId);
        Assert.Equal(IncidentClassification.Known, handoff.Classification);
        Assert.Equal("known-demo-workload-crashloop", handoff.MatchedPatternName);
        Assert.Equal("RestartDemoWorkload", handoff.ProposedActionType);
        Assert.False(handoff.AutoExecutionAllowed);
        Assert.Null(handoff.ExecutionOutcome);

        IncidentLifecycleEvent shared = Assert.Single(
            _publisher.Events, item => item.EventType == "Tier1RuleHandoffShared");
        Assert.NotNull(shared.Details);
        Assert.Equal("Demo incident", shared.Details!["incidentTitle"]);
        Assert.Equal("known-demo-workload-crashloop", shared.Details["ruleMatchedPattern"]);
        Assert.Equal("The action requires human approval.", shared.Details["escalationReason"]);
    }

    [Fact]
    public async Task Tier2Plan_WithoutModelRequestedApproval_StillAsksAHuman()
    {
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.Tier1Results.Enqueue(() => WorkflowTestData.Tier1Result(AgentDisposition.Escalate, 0.4));
        _activities.Tier2Results.Enqueue(() => WorkflowTestData.Plan(requiresApproval: false));
        _approvalGate.Decisions.Enqueue(new ApprovalDecision(ApprovalOutcome.Rejected, "oncall", "Not now"));

        IncidentWorkflowResult result = await RunAsync();

        Assert.Equal(IncidentWorkflowState.Rejected, result.FinalState);
        Assert.Empty(_activities.ExecutedActions);
    }

    [Fact]
    public async Task ApprovalRejected_EndsInRejected()
    {
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.Tier1Results.Enqueue(() => WorkflowTestData.Tier1Result(AgentDisposition.Escalate, 0.4));
        _activities.Tier2Results.Enqueue(() => WorkflowTestData.Plan(requiresApproval: true));
        _approvalGate.Decisions.Enqueue(new ApprovalDecision(ApprovalOutcome.Rejected, "oncall", "Too risky"));

        IncidentWorkflowResult result = await RunAsync();

        Assert.Equal(IncidentWorkflowState.Rejected, result.FinalState);
        Assert.Empty(_activities.ExecutedActions);
    }

    [Fact]
    public async Task ApprovalTimeout_EndsInTerminated()
    {
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.Tier1Results.Enqueue(() => WorkflowTestData.Tier1Result(AgentDisposition.Escalate, 0.4));
        _activities.Tier2Results.Enqueue(() => WorkflowTestData.Plan(requiresApproval: true));

        IncidentWorkflowResult result = await RunAsync(
            IncidentWorkflowOptions.Default with { ApprovalTimeout = TimeSpan.FromMinutes(5) });

        Assert.Equal(IncidentWorkflowState.Terminated, result.FinalState);
        Assert.Empty(_activities.ExecutedActions);
        Assert.Equal(TimeSpan.FromMinutes(5), _approvalGate.ObservedTimeouts.Single());
    }

    [Fact]
    public async Task ExecutionFailure_WithRollback_RollsBackAndFails()
    {
        RemediationAction planAction = WorkflowTestData.Action("rollback_deployment", "inc-001-plan-1");
        RemediationAction rollbackAction = WorkflowTestData.Action("restart_deployment", "inc-001-rb-1");
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.Tier1Results.Enqueue(() => WorkflowTestData.Tier1Result(AgentDisposition.Escalate, 0.4));
        _activities.Tier2Results.Enqueue(() =>
            WorkflowTestData.Plan(requiresApproval: false, actions: [planAction], rollback: [rollbackAction]));
        _approvalGate.Decisions.Enqueue(new ApprovalDecision(ApprovalOutcome.Approved, "oncall", "Reviewed"));
        _activities.ExecutionResults.Enqueue(action =>
            WorkflowTestData.Execution("inc-001", action, ExecutionOutcome.Failed, "boom"));
        _activities.ExecutionResults.Enqueue(action =>
            WorkflowTestData.Execution("inc-001", action, ExecutionOutcome.Failed, "boom again"));
        _activities.ExecutionResults.Enqueue(action =>
            WorkflowTestData.Execution("inc-001", action, ExecutionOutcome.Succeeded, "rollback done"));

        IncidentWorkflowResult result = await RunAsync();

        Assert.Equal(IncidentWorkflowState.Failed, result.FinalState);
        Assert.Contains(IncidentWorkflowState.RollingBack, result.StateHistory);
        Assert.Equal(3, _activities.ExecutedActions.Count);
        Assert.Equal(rollbackAction.IdempotencyKey, _activities.ExecutedActions[^1].Action.IdempotencyKey);
    }

    [Fact]
    public async Task ExecutionRejectedByPolicy_IsNotRetried()
    {
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.Tier1Results.Enqueue(() => WorkflowTestData.Tier1Result(AgentDisposition.Escalate, 0.4));
        _activities.Tier2Results.Enqueue(() => WorkflowTestData.Plan(requiresApproval: false));
        _approvalGate.Decisions.Enqueue(new ApprovalDecision(ApprovalOutcome.Approved, "oncall", "Reviewed"));
        _activities.ExecutionResults.Enqueue(action =>
            WorkflowTestData.Execution("inc-001", action, ExecutionOutcome.Rejected, "policy rejected"));

        IncidentWorkflowResult result = await RunAsync();

        Assert.Equal(IncidentWorkflowState.Failed, result.FinalState);
        Assert.Single(_activities.ExecutedActions);
    }

    [Fact]
    public async Task VerificationFailure_AfterTier1Remediation_EscalatesToTier2()
    {
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.Tier1Results.Enqueue(() =>
            WorkflowTestData.Tier1Result(AgentDisposition.Resolve, 0.95, WorkflowTestData.Action()));
        _activities.VerificationResults.Enqueue(() => WorkflowTestData.Verification(VerificationOutcome.Failed));
        _activities.Tier2Results.Enqueue(() => WorkflowTestData.Plan(requiresApproval: false));
        _activities.VerificationResults.Enqueue(() => WorkflowTestData.Verification(VerificationOutcome.Passed));

        IncidentWorkflowResult result = await RunAsync(AutonomousDemoOptions);

        Assert.Equal(IncidentWorkflowState.Resolved, result.FinalState);
        Assert.Equal(1, _activities.Tier2Invocations);
        Assert.Contains(IncidentWorkflowState.Tier2Investigation, result.StateHistory);
    }

    [Fact]
    public async Task VerificationFailure_WithNoTier2AttemptsRemaining_FailsSafely()
    {
        var options = IncidentWorkflowOptions.Default with { MaxTier2Attempts = 1, MaxVerificationAttempts = 1 };
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.Tier1Results.Enqueue(() => WorkflowTestData.Tier1Result(AgentDisposition.Escalate, 0.4));
        _activities.Tier2Results.Enqueue(() => WorkflowTestData.Plan(requiresApproval: false));
        _approvalGate.Decisions.Enqueue(new ApprovalDecision(ApprovalOutcome.Approved, "oncall", "Reviewed"));
        _activities.VerificationResults.Enqueue(() => WorkflowTestData.Verification(VerificationOutcome.Failed));

        IncidentWorkflowResult result = await RunAsync(options);

        Assert.Equal(IncidentWorkflowState.Failed, result.FinalState);
        Assert.Equal(1, _activities.Tier2Invocations);
    }

    [Fact]
    public async Task RequestMoreEvidence_CollectsAndReinvestigates_Bounded()
    {
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence(), WorkflowTestData.Evidence("ev-002")]);
        _activities.Tier1Results.Enqueue(() => WorkflowTestData.Tier1Result(AgentDisposition.RequestMoreEvidence, 0.3));
        _activities.Tier1Results.Enqueue(() =>
            WorkflowTestData.Tier1Result(AgentDisposition.Resolve, 0.95, WorkflowTestData.Action()));
        _activities.VerificationResults.Enqueue(() => WorkflowTestData.Verification(VerificationOutcome.Passed));

        IncidentWorkflowResult result = await RunAsync(AutonomousDemoOptions);

        Assert.Equal(IncidentWorkflowState.Resolved, result.FinalState);
        Assert.Equal(2, _activities.Tier1Invocations);
        Assert.Contains(IncidentWorkflowState.AwaitingEvidence, result.StateHistory);
    }

    [Fact]
    public async Task Tier1MaxAttemptsExceeded_ByRepeatedFailures_TerminatesSafely()
    {
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.Tier1Results.Enqueue(() => throw new InvalidOperationException("model unavailable"));
        _activities.Tier1Results.Enqueue(() => throw new InvalidOperationException("model unavailable"));

        IncidentWorkflowResult result = await RunAsync();

        Assert.Equal(IncidentWorkflowState.Failed, result.FinalState);
        Assert.Equal(2, _activities.Tier1Invocations);
    }

    [Fact]
    public async Task InconclusiveVerification_NeverCountsAsSuccess()
    {
        var options = IncidentWorkflowOptions.Default with { MaxTier2Attempts = 1, MaxVerificationAttempts = 1 };
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.Tier1Results.Enqueue(() => WorkflowTestData.Tier1Result(AgentDisposition.Escalate, 0.4));
        _activities.Tier2Results.Enqueue(() => WorkflowTestData.Plan(requiresApproval: false));
        _approvalGate.Decisions.Enqueue(new ApprovalDecision(ApprovalOutcome.Approved, "oncall", "Reviewed"));
        _activities.VerificationResults.Enqueue(() => WorkflowTestData.Verification(VerificationOutcome.Inconclusive));

        IncidentWorkflowResult result = await RunAsync(options);

        Assert.Equal(IncidentWorkflowState.Failed, result.FinalState);
    }

    [Fact]
    public async Task LifecycleEvents_CarryCorrelationData()
    {
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.Tier1Results.Enqueue(() =>
            WorkflowTestData.Tier1Result(AgentDisposition.Resolve, 0.95, WorkflowTestData.Action()));
        _activities.Tier2Results.Enqueue(() => WorkflowTestData.Plan(requiresApproval: true));
        _approvalGate.Decisions.Enqueue(new ApprovalDecision(ApprovalOutcome.Approved, "oncall", "Reviewed"));
        _activities.VerificationResults.Enqueue(() => WorkflowTestData.Verification(VerificationOutcome.Passed));

        await RunAsync();

        Assert.NotEmpty(_publisher.Events);
        Assert.All(_publisher.Events, item =>
        {
            Assert.Equal("inc-001", item.IncidentId);
            Assert.Equal("corr-001", item.CorrelationId);
            Assert.Equal("wf-001", item.WorkflowInstanceId);
            Assert.Equal("IncidentWorkflow", item.Component);
        });
        Assert.Contains(_publisher.Events, item => item.EventType == "IncidentReceived");
        Assert.Contains(_publisher.Events, item => item.EventType == "ExecutionCompleted");
        Assert.Contains(_publisher.Events, item =>
            item.EventType == "StateChanged" && item.Outcome == nameof(IncidentWorkflowState.Resolved));
    }

    [Fact]
    public async Task PublisherFailure_DoesNotBlockRemediation()
    {
        var throwingPublisher = new ThrowingLifecycleEventPublisher();
        var orchestrator = new IncidentWorkflowOrchestrator(_activities, _approvalGate, throwingPublisher);
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.Tier1Results.Enqueue(() =>
            WorkflowTestData.Tier1Result(AgentDisposition.Resolve, 0.95, WorkflowTestData.Action()));
        _activities.Tier2Results.Enqueue(() => WorkflowTestData.Plan(requiresApproval: true));
        _approvalGate.Decisions.Enqueue(new ApprovalDecision(ApprovalOutcome.Approved, "oncall", "Reviewed"));
        _activities.VerificationResults.Enqueue(() => WorkflowTestData.Verification(VerificationOutcome.Passed));

        IncidentWorkflowResult result = await orchestrator.RunAsync(
            WorkflowTestData.Incident(), "wf-001", "corr-001", CancellationToken.None);

        Assert.Equal(IncidentWorkflowState.Resolved, result.FinalState);
    }

    private sealed class ThrowingLifecycleEventPublisher : ILifecycleEventPublisher
    {
        public Task PublishAsync(IncidentLifecycleEvent lifecycleEvent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("pubsub unavailable");
    }
}
