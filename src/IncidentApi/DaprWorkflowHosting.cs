using AzureAgenticOps.Contracts;
using AzureAgenticOps.IncidentWorkflow;
using AzureAgenticOps.RuleEvaluator;
using Dapr.Workflow;

namespace AzureAgenticOps.IncidentApi;

/// <summary>
/// Options selecting the workflow hosting engine. The default engine runs the
/// deterministic orchestrator in-process so the system stays verifiable without
/// any sidecar; the Dapr engine hosts the same orchestrator as a durable Dapr
/// Workflow with replay-safe activities and external approval events.
/// </summary>
public sealed class WorkflowHostingOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Workflow";

    /// <summary>The in-process engine name (default).</summary>
    public const string InProcessEngine = "InProcess";

    /// <summary>The Dapr Workflow engine name.</summary>
    public const string DaprEngine = "Dapr";

    /// <summary>Gets or sets the engine name. Must be <c>InProcess</c> or <c>Dapr</c>.</summary>
    public string Engine { get; set; } = InProcessEngine;

    /// <summary>Gets whether the Dapr Workflow engine is selected.</summary>
    public bool UsesDaprEngine =>
        string.Equals(Engine, DaprEngine, StringComparison.OrdinalIgnoreCase);

    /// <summary>Validates the options for startup.</summary>
    /// <param name="error">The validation failure description, when invalid.</param>
    /// <returns><c>true</c> when the options are valid; otherwise <c>false</c>.</returns>
    public bool Validate(out string? error)
    {
        if (!string.Equals(Engine, InProcessEngine, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Engine, DaprEngine, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Workflow:Engine '{Engine}' is not a known engine. Valid values: InProcess, Dapr.";
            return false;
        }

        error = null;
        return true;
    }
}

/// <summary>
/// Runs incident workflows and delivers external approval events, hiding the
/// hosting engine from the API surface. Implementations must be safe for
/// concurrent runs of different incidents.
/// </summary>
public interface IIncidentWorkflowRunner
{
    /// <summary>Runs the incident workflow to completion.</summary>
    /// <param name="incident">The submitted incident.</param>
    /// <param name="workflowInstanceId">The workflow instance identifier.</param>
    /// <param name="correlationId">The correlation identifier for all related operations.</param>
    /// <param name="cancellationToken">A token to cancel waiting for the result.</param>
    /// <returns>The final workflow result.</returns>
    Task<IncidentWorkflowResult> RunAsync(
        Incident incident,
        string workflowInstanceId,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>Delivers a human approval decision to a running workflow.</summary>
    /// <param name="incidentId">The incident awaiting approval.</param>
    /// <param name="workflowInstanceId">The workflow instance identifier.</param>
    /// <param name="decision">The human decision.</param>
    /// <param name="cancellationToken">A token to cancel the delivery.</param>
    /// <returns>Whether the decision was accepted for delivery.</returns>
    Task<bool> TryDeliverApprovalAsync(
        string incidentId,
        string workflowInstanceId,
        ApprovalDecision decision,
        CancellationToken cancellationToken);
}

/// <summary>
/// The default runner: executes the deterministic orchestrator in-process with
/// the in-memory approval gate. Requires no sidecar or external services.
/// </summary>
public sealed class InProcessIncidentWorkflowRunner : IIncidentWorkflowRunner
{
    private readonly IncidentWorkflowOrchestrator _orchestrator;
    private readonly ExternalEventApprovalGate _approvalGate;

    /// <summary>Initializes a new runner.</summary>
    /// <param name="orchestrator">The in-process orchestrator.</param>
    /// <param name="approvalGate">The in-memory approval gate.</param>
    public InProcessIncidentWorkflowRunner(
        IncidentWorkflowOrchestrator orchestrator,
        ExternalEventApprovalGate approvalGate)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(approvalGate);
        _orchestrator = orchestrator;
        _approvalGate = approvalGate;
    }

    /// <inheritdoc />
    public Task<IncidentWorkflowResult> RunAsync(
        Incident incident,
        string workflowInstanceId,
        string correlationId,
        CancellationToken cancellationToken) =>
        _orchestrator.RunAsync(incident, workflowInstanceId, correlationId, cancellationToken);

    /// <inheritdoc />
    public Task<bool> TryDeliverApprovalAsync(
        string incidentId,
        string workflowInstanceId,
        ApprovalDecision decision,
        CancellationToken cancellationToken) =>
        Task.FromResult(_approvalGate.TryDeliver(incidentId, decision));
}

/// <summary>
/// A runner that schedules the incident workflow on the Dapr Workflow engine
/// through the sidecar and waits for durable completion. Approval decisions are
/// raised as workflow external events, so no HTTP request is ever held open and
/// a delayed approval survives process restarts.
/// </summary>
public sealed class DaprIncidentWorkflowRunner : IIncidentWorkflowRunner
{
    private readonly DaprWorkflowClient _workflowClient;
    private readonly IncidentWorkflowOptions _workflowOptions;

    /// <summary>Initializes a new runner.</summary>
    /// <param name="workflowClient">The Dapr workflow client.</param>
    /// <param name="workflowOptions">The bounded workflow options passed to each instance.</param>
    public DaprIncidentWorkflowRunner(
        DaprWorkflowClient workflowClient,
        IncidentWorkflowOptions workflowOptions)
    {
        ArgumentNullException.ThrowIfNull(workflowClient);
        ArgumentNullException.ThrowIfNull(workflowOptions);
        _workflowClient = workflowClient;
        _workflowOptions = workflowOptions;
    }

    /// <inheritdoc />
    public async Task<IncidentWorkflowResult> RunAsync(
        Incident incident,
        string workflowInstanceId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);

        var input = new DaprIncidentWorkflowInput(incident, correlationId, _workflowOptions);
        await _workflowClient
            .ScheduleNewWorkflowAsync(nameof(DaprIncidentWorkflow), workflowInstanceId, input)
            .ConfigureAwait(false);

        WorkflowState state = await _workflowClient
            .WaitForWorkflowCompletionAsync(workflowInstanceId, getInputsAndOutputs: true, cancellationToken)
            .ConfigureAwait(false);

        IncidentWorkflowResult? result = state.RuntimeStatus == WorkflowRuntimeStatus.Completed
            ? state.ReadOutputAs<IncidentWorkflowResult>()
            : null;
        if (result is null)
        {
            throw new InvalidOperationException(
                $"Dapr workflow '{workflowInstanceId}' for incident '{incident.IncidentId}' " +
                $"ended in status '{state.RuntimeStatus}' without a workflow result.");
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> TryDeliverApprovalAsync(
        string incidentId,
        string workflowInstanceId,
        ApprovalDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        await _workflowClient
            .RaiseEventAsync(workflowInstanceId, DaprIncidentWorkflow.ApprovalEventName, decision, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }
}

/// <summary>
/// The serialized input of one durable incident workflow instance.
/// </summary>
/// <param name="Incident">The incident under investigation.</param>
/// <param name="CorrelationId">The correlation identifier for all related operations.</param>
/// <param name="Options">The bounded workflow options for the instance.</param>
public sealed record DaprIncidentWorkflowInput(
    Incident Incident,
    string CorrelationId,
    IncidentWorkflowOptions Options);

/// <summary>
/// The durable Dapr Workflow hosting the deterministic incident orchestrator.
/// The orchestrator logic is reused unchanged: every activity call is routed
/// through <see cref="WorkflowContext.CallActivityAsync{T}"/> so each side
/// effect is journaled and replay-safe, the approval wait becomes a durable
/// external event, lifecycle publishing becomes an activity, and workflow time
/// is read from the replay-safe <see cref="WorkflowContext.CurrentUtcDateTime"/>.
/// Metrics are recorded inside activities only, never in orchestrator code, so
/// replays cannot double-count.
/// </summary>
public sealed class DaprIncidentWorkflow : Workflow<DaprIncidentWorkflowInput, IncidentWorkflowResult>
{
    /// <summary>The external event name carrying human approval decisions.</summary>
    public const string ApprovalEventName = "approval-decision";

    /// <inheritdoc />
    public override Task<IncidentWorkflowResult> RunAsync(WorkflowContext context, DaprIncidentWorkflowInput input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);

        var orchestrator = new IncidentWorkflowOrchestrator(
            new ContextActivities(context),
            new ContextApprovalGate(context),
            new ContextLifecyclePublisher(context),
            input.Options,
            new WorkflowTimeProvider(context),
            metrics: null);
        return orchestrator.RunAsync(input.Incident, context.InstanceId, input.CorrelationId, CancellationToken.None);
    }

    /// <summary>A replay-safe time provider backed by the workflow context clock.</summary>
    private sealed class WorkflowTimeProvider : TimeProvider
    {
        private readonly WorkflowContext _context;

        public WorkflowTimeProvider(WorkflowContext context) => _context = context;

        public override DateTimeOffset GetUtcNow() =>
            new(DateTime.SpecifyKind(_context.CurrentUtcDateTime, DateTimeKind.Utc));
    }

    /// <summary>Routes orchestrator activity calls to journaled Dapr activities.</summary>
    private sealed class ContextActivities : IIncidentWorkflowActivities
    {
        private readonly WorkflowContext _context;

        public ContextActivities(WorkflowContext context) => _context = context;

        public async Task<IReadOnlyList<IncidentEvidence>> CollectEvidenceAsync(
            Incident incident, int attemptNumber, string correlationId, CancellationToken cancellationToken) =>
            await _context.CallActivityAsync<IncidentEvidence[]>(
                nameof(CollectEvidenceActivity),
                new CollectEvidenceActivityInput(incident, attemptNumber, correlationId)).ConfigureAwait(true);

        public Task<RuleEvaluationResult> EvaluateRulesAsync(
            Incident incident, IReadOnlyList<IncidentEvidence> evidence, string correlationId, CancellationToken cancellationToken) =>
            _context.CallActivityAsync<RuleEvaluationResult>(
                nameof(EvaluateRulesActivity),
                new EvaluateRulesActivityInput(incident, [.. evidence], correlationId));

        public Task<RuleRemediationDecision> PrepareRuleRemediationAsync(
            Incident incident, RuleEvaluationResult ruleResult, string correlationId, CancellationToken cancellationToken) =>
            _context.CallActivityAsync<RuleRemediationDecision>(
                nameof(PrepareRuleRemediationActivity),
                new PrepareRuleRemediationActivityInput(incident, ruleResult, correlationId));

        public Task<InvestigationResult> RunTier1InvestigationAsync(
            Incident incident, IReadOnlyList<IncidentEvidence> evidence, RuleHandlingSummary ruleHandling, string correlationId, CancellationToken cancellationToken) =>
            _context.CallActivityAsync<InvestigationResult>(
                nameof(Tier1InvestigationActivity),
                new Tier1InvestigationActivityInput(incident, [.. evidence], ruleHandling, correlationId));

        public Task<RemediationPlan> RunTier2PlanningAsync(
            Incident incident, InvestigationResult tier1Handoff, IReadOnlyList<IncidentEvidence> evidence, string correlationId, CancellationToken cancellationToken) =>
            _context.CallActivityAsync<RemediationPlan>(
                nameof(Tier2PlanningActivity),
                new Tier2PlanningActivityInput(incident, tier1Handoff, [.. evidence], correlationId));

        public Task<ExecutionResult> ExecuteActionAsync(
            Incident incident, RemediationAction action, bool approvalGranted, string correlationId, CancellationToken cancellationToken) =>
            _context.CallActivityAsync<ExecutionResult>(
                nameof(ExecuteActionActivity),
                new ExecuteActionActivityInput(incident, action, approvalGranted, correlationId));

        public Task<VerificationResult> VerifyTier1RemediationAsync(
            Incident incident, RemediationAction executedAction, string correlationId, CancellationToken cancellationToken) =>
            _context.CallActivityAsync<VerificationResult>(
                nameof(VerifyTier1RemediationActivity),
                new VerifyTier1RemediationActivityInput(incident, executedAction, correlationId));

        public Task<VerificationResult> VerifyPlanAsync(
            Incident incident, RemediationPlan plan, string correlationId, CancellationToken cancellationToken) =>
            _context.CallActivityAsync<VerificationResult>(
                nameof(VerifyPlanActivity),
                new VerifyPlanActivityInput(incident, plan, correlationId));
    }

    /// <summary>Waits for the durable approval external event with a bounded timeout.</summary>
    private sealed class ContextApprovalGate : IApprovalGate
    {
        private readonly WorkflowContext _context;

        public ContextApprovalGate(WorkflowContext context) => _context = context;

        public async Task<ApprovalDecision> WaitForApprovalAsync(
            Incident incident, RemediationPlan plan, TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                return await _context
                    .WaitForExternalEventAsync<ApprovalDecision>(ApprovalEventName, timeout)
                    .ConfigureAwait(true);
            }
            catch (TaskCanceledException)
            {
                return new ApprovalDecision(ApprovalOutcome.TimedOut);
            }
        }
    }

    /// <summary>Publishes lifecycle events through a journaled activity.</summary>
    private sealed class ContextLifecyclePublisher : ILifecycleEventPublisher
    {
        private readonly WorkflowContext _context;

        public ContextLifecyclePublisher(WorkflowContext context) => _context = context;

        public Task PublishAsync(IncidentLifecycleEvent lifecycleEvent, CancellationToken cancellationToken) =>
            _context.CallActivityAsync<bool>(nameof(PublishLifecycleEventActivity), lifecycleEvent);
    }
}

/// <summary>The input of <see cref="CollectEvidenceActivity"/>.</summary>
/// <param name="Incident">The incident under investigation.</param>
/// <param name="AttemptNumber">The collection attempt number, starting at 1.</param>
/// <param name="CorrelationId">The correlation identifier for observability.</param>
public sealed record CollectEvidenceActivityInput(Incident Incident, int AttemptNumber, string CorrelationId);

/// <summary>The input of <see cref="EvaluateRulesActivity"/>.</summary>
/// <param name="Incident">The incident under investigation.</param>
/// <param name="Evidence">The evidence collected for the incident.</param>
/// <param name="CorrelationId">The correlation identifier for observability.</param>
public sealed record EvaluateRulesActivityInput(Incident Incident, IncidentEvidence[] Evidence, string CorrelationId);

/// <summary>The input of <see cref="PrepareRuleRemediationActivity"/>.</summary>
/// <param name="Incident">The incident under investigation.</param>
/// <param name="RuleResult">The deterministic rule evaluation result with a proposed action type.</param>
/// <param name="CorrelationId">The correlation identifier for observability.</param>
public sealed record PrepareRuleRemediationActivityInput(Incident Incident, RuleEvaluationResult RuleResult, string CorrelationId);

/// <summary>The input of <see cref="Tier1InvestigationActivity"/>.</summary>
/// <param name="Incident">The incident under investigation.</param>
/// <param name="Evidence">The evidence collected for the incident.</param>
/// <param name="RuleHandling">The deterministic summary of the rule-based handling shared with Tier 1.</param>
/// <param name="CorrelationId">The correlation identifier for observability.</param>
public sealed record Tier1InvestigationActivityInput(Incident Incident, IncidentEvidence[] Evidence, RuleHandlingSummary RuleHandling, string CorrelationId);

/// <summary>The input of <see cref="Tier2PlanningActivity"/>.</summary>
/// <param name="Incident">The incident under investigation.</param>
/// <param name="Tier1Handoff">The complete structured Tier 1 handoff.</param>
/// <param name="Evidence">The evidence collected for the incident.</param>
/// <param name="CorrelationId">The correlation identifier for observability.</param>
public sealed record Tier2PlanningActivityInput(Incident Incident, InvestigationResult Tier1Handoff, IncidentEvidence[] Evidence, string CorrelationId);

/// <summary>The input of <see cref="ExecuteActionActivity"/>.</summary>
/// <param name="Incident">The incident the action belongs to.</param>
/// <param name="Action">The remediation action to execute.</param>
/// <param name="ApprovalGranted">Whether human approval has been granted.</param>
/// <param name="CorrelationId">The correlation identifier for observability.</param>
public sealed record ExecuteActionActivityInput(Incident Incident, RemediationAction Action, bool ApprovalGranted, string CorrelationId);

/// <summary>The input of <see cref="VerifyTier1RemediationActivity"/>.</summary>
/// <param name="Incident">The incident that was remediated.</param>
/// <param name="ExecutedAction">The action that was executed.</param>
/// <param name="CorrelationId">The correlation identifier for observability.</param>
public sealed record VerifyTier1RemediationActivityInput(Incident Incident, RemediationAction ExecutedAction, string CorrelationId);

/// <summary>The input of <see cref="VerifyPlanActivity"/>.</summary>
/// <param name="Incident">The incident that was remediated.</param>
/// <param name="Plan">The executed remediation plan.</param>
/// <param name="CorrelationId">The correlation identifier for observability.</param>
public sealed record VerifyPlanActivityInput(Incident Incident, RemediationPlan Plan, string CorrelationId);

/// <summary>Collects evidence through the shared activity implementation.</summary>
public sealed class CollectEvidenceActivity : WorkflowActivity<CollectEvidenceActivityInput, IncidentEvidence[]>
{
    private readonly IIncidentWorkflowActivities _activities;

    /// <summary>Initializes a new activity.</summary>
    /// <param name="activities">The shared activity implementation.</param>
    public CollectEvidenceActivity(IIncidentWorkflowActivities activities) => _activities = activities;

    /// <inheritdoc />
    public override async Task<IncidentEvidence[]> RunAsync(WorkflowActivityContext context, CollectEvidenceActivityInput input) =>
        [.. await _activities.CollectEvidenceAsync(input.Incident, input.AttemptNumber, input.CorrelationId, CancellationToken.None).ConfigureAwait(false)];
}

/// <summary>Runs deterministic rule evaluation through the shared activity implementation.</summary>
public sealed class EvaluateRulesActivity : WorkflowActivity<EvaluateRulesActivityInput, RuleEvaluationResult>
{
    private readonly IIncidentWorkflowActivities _activities;

    /// <summary>Initializes a new activity.</summary>
    /// <param name="activities">The shared activity implementation.</param>
    public EvaluateRulesActivity(IIncidentWorkflowActivities activities) => _activities = activities;

    /// <inheritdoc />
    public override Task<RuleEvaluationResult> RunAsync(WorkflowActivityContext context, EvaluateRulesActivityInput input) =>
        _activities.EvaluateRulesAsync(input.Incident, input.Evidence, input.CorrelationId, CancellationToken.None);
}

/// <summary>Prepares the rule fast-path remediation decision through the shared activity implementation.</summary>
public sealed class PrepareRuleRemediationActivity : WorkflowActivity<PrepareRuleRemediationActivityInput, RuleRemediationDecision>
{
    private readonly IIncidentWorkflowActivities _activities;

    /// <summary>Initializes a new activity.</summary>
    /// <param name="activities">The shared activity implementation.</param>
    public PrepareRuleRemediationActivity(IIncidentWorkflowActivities activities) => _activities = activities;

    /// <inheritdoc />
    public override Task<RuleRemediationDecision> RunAsync(WorkflowActivityContext context, PrepareRuleRemediationActivityInput input) =>
        _activities.PrepareRuleRemediationAsync(input.Incident, input.RuleResult, input.CorrelationId, CancellationToken.None);
}

/// <summary>Runs the Tier 1 investigation through the shared activity implementation.</summary>
public sealed class Tier1InvestigationActivity : WorkflowActivity<Tier1InvestigationActivityInput, InvestigationResult>
{
    private readonly IIncidentWorkflowActivities _activities;

    /// <summary>Initializes a new activity.</summary>
    /// <param name="activities">The shared activity implementation.</param>
    public Tier1InvestigationActivity(IIncidentWorkflowActivities activities) => _activities = activities;

    /// <inheritdoc />
    public override Task<InvestigationResult> RunAsync(WorkflowActivityContext context, Tier1InvestigationActivityInput input) =>
        _activities.RunTier1InvestigationAsync(input.Incident, input.Evidence, input.RuleHandling, input.CorrelationId, CancellationToken.None);
}

/// <summary>Runs Tier 2 planning through the shared activity implementation.</summary>
public sealed class Tier2PlanningActivity : WorkflowActivity<Tier2PlanningActivityInput, RemediationPlan>
{
    private readonly IIncidentWorkflowActivities _activities;

    /// <summary>Initializes a new activity.</summary>
    /// <param name="activities">The shared activity implementation.</param>
    public Tier2PlanningActivity(IIncidentWorkflowActivities activities) => _activities = activities;

    /// <inheritdoc />
    public override Task<RemediationPlan> RunAsync(WorkflowActivityContext context, Tier2PlanningActivityInput input) =>
        _activities.RunTier2PlanningAsync(input.Incident, input.Tier1Handoff, input.Evidence, input.CorrelationId, CancellationToken.None);
}

/// <summary>Executes one validated remediation action through the shared activity implementation.</summary>
public sealed class ExecuteActionActivity : WorkflowActivity<ExecuteActionActivityInput, ExecutionResult>
{
    private readonly IIncidentWorkflowActivities _activities;

    /// <summary>Initializes a new activity.</summary>
    /// <param name="activities">The shared activity implementation.</param>
    public ExecuteActionActivity(IIncidentWorkflowActivities activities) => _activities = activities;

    /// <inheritdoc />
    public override Task<ExecutionResult> RunAsync(WorkflowActivityContext context, ExecuteActionActivityInput input) =>
        _activities.ExecuteActionAsync(input.Incident, input.Action, input.ApprovalGranted, input.CorrelationId, CancellationToken.None);
}

/// <summary>Verifies a Tier 1 fast-path remediation through the shared activity implementation.</summary>
public sealed class VerifyTier1RemediationActivity : WorkflowActivity<VerifyTier1RemediationActivityInput, VerificationResult>
{
    private readonly IIncidentWorkflowActivities _activities;

    /// <summary>Initializes a new activity.</summary>
    /// <param name="activities">The shared activity implementation.</param>
    public VerifyTier1RemediationActivity(IIncidentWorkflowActivities activities) => _activities = activities;

    /// <inheritdoc />
    public override Task<VerificationResult> RunAsync(WorkflowActivityContext context, VerifyTier1RemediationActivityInput input) =>
        _activities.VerifyTier1RemediationAsync(input.Incident, input.ExecutedAction, input.CorrelationId, CancellationToken.None);
}

/// <summary>Runs the verification steps of a Tier 2 plan through the shared activity implementation.</summary>
public sealed class VerifyPlanActivity : WorkflowActivity<VerifyPlanActivityInput, VerificationResult>
{
    private readonly IIncidentWorkflowActivities _activities;

    /// <summary>Initializes a new activity.</summary>
    /// <param name="activities">The shared activity implementation.</param>
    public VerifyPlanActivity(IIncidentWorkflowActivities activities) => _activities = activities;

    /// <inheritdoc />
    public override Task<VerificationResult> RunAsync(WorkflowActivityContext context, VerifyPlanActivityInput input) =>
        _activities.VerifyPlanAsync(input.Incident, input.Plan, input.CorrelationId, CancellationToken.None);
}

/// <summary>
/// Publishes one lifecycle event to the configured publishers. Publishing runs
/// as an activity so it is journaled and never re-executed on replay.
/// </summary>
public sealed class PublishLifecycleEventActivity : WorkflowActivity<IncidentLifecycleEvent, bool>
{
    private readonly ILifecycleEventPublisher _publisher;

    /// <summary>Initializes a new activity.</summary>
    /// <param name="publisher">The lifecycle event publisher.</param>
    public PublishLifecycleEventActivity(ILifecycleEventPublisher publisher) => _publisher = publisher;

    /// <inheritdoc />
    public override async Task<bool> RunAsync(WorkflowActivityContext context, IncidentLifecycleEvent input)
    {
        await _publisher.PublishAsync(input, CancellationToken.None).ConfigureAwait(false);
        return true;
    }
}
