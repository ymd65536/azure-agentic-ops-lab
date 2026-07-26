using System.Diagnostics;
using System.Diagnostics.Metrics;
using AzureAgenticOps.Contracts;
using AzureAgenticOps.IncidentWorkflow;
using AzureAgenticOps.Observability;

namespace AzureAgenticOps.WorkflowTests;

public sealed class OrchestratorObservabilityTests : IDisposable
{
    private const string CorrelationId = "corr-obs-001";

    private readonly FakeWorkflowActivities _activities = new();
    private readonly FakeApprovalGate _approvalGate = new();
    private readonly InMemoryLifecycleEventPublisher _publisher = new();
    private readonly AgenticOpsMetrics _metrics = new();
    private readonly List<Activity> _spans = [];
    private readonly List<string> _instruments = [];
    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener;

    public OrchestratorObservabilityTests()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ObservabilityNames.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                // Other test classes run in parallel and emit through the same
                // static source; keep only spans from this class's runs.
                if (Equals(activity.GetTagItem(ObservabilityTags.CorrelationId), CorrelationId))
                {
                    _spans.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == ObservabilityNames.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, _, _, _) => _instruments.Add(instrument.Name));
        _meterListener.SetMeasurementEventCallback<double>((instrument, _, _, _) => _instruments.Add(instrument.Name));
        _meterListener.Start();
    }

    public void Dispose()
    {
        _activityListener.Dispose();
        _meterListener.Dispose();
        _metrics.Dispose();
    }

    private Task<IncidentWorkflowResult> RunAsync(IncidentWorkflowOptions? options = null) =>
        new IncidentWorkflowOrchestrator(_activities, _approvalGate, _publisher, options, metrics: _metrics)
            .RunAsync(WorkflowTestData.Incident(), "wf-obs-001", CorrelationId, CancellationToken.None);

    [Fact]
    public async Task Tier1FastPath_EmitsWorkflowSpanAndResolvedMetrics()
    {
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.Tier1Results.Enqueue(() =>
            WorkflowTestData.Tier1Result(AgentDisposition.Resolve, 0.95, WorkflowTestData.Action()));
        _activities.VerificationResults.Enqueue(() => WorkflowTestData.Verification(VerificationOutcome.Passed));

        IncidentWorkflowResult result = await RunAsync(IncidentWorkflowOptions.Default with
        {
            Tier1PlansRequireTier2RiskAssessment = false,
            Tier2PlansAlwaysRequireApproval = false,
        });

        Assert.Equal(IncidentWorkflowState.Resolved, result.FinalState);

        Activity workflowSpan = Assert.Single(_spans, s => s.OperationName == SpanNames.WorkflowExecution);
        Assert.Equal(result.IncidentId, workflowSpan.GetTagItem(ObservabilityTags.IncidentId));
        Assert.Equal("wf-obs-001", workflowSpan.GetTagItem(ObservabilityTags.WorkflowInstanceId));
        Assert.Equal("corr-obs-001", workflowSpan.GetTagItem(ObservabilityTags.CorrelationId));
        Assert.Equal("Resolved", workflowSpan.GetTagItem(ObservabilityTags.FinalState));
        Assert.Equal(ActivityStatusCode.Ok, workflowSpan.Status);

        Assert.Contains(_spans, s => s.OperationName == SpanNames.EvidenceCollection);
        Assert.Contains(_spans, s => s.OperationName == SpanNames.RuleEvaluation);
        Assert.Contains(_spans, s => s.OperationName == SpanNames.Tier1Investigation);
        Assert.Contains(_spans, s => s.OperationName == SpanNames.Execution);
        Assert.Contains(_spans, s => s.OperationName == SpanNames.Verification);
        Assert.DoesNotContain(_spans, s => s.OperationName == SpanNames.Tier2Planning);

        Assert.Contains("incident_total", _instruments);
        Assert.Contains("incident_resolved_total", _instruments);
        Assert.Contains("incident_duration_seconds", _instruments);
        Assert.Contains("tier1_duration_seconds", _instruments);
        Assert.Contains("action_execution_total", _instruments);
        Assert.DoesNotContain("incident_escalated_total", _instruments);
        Assert.DoesNotContain("incident_failed_total", _instruments);
    }

    [Fact]
    public async Task Tier2Escalation_EmitsEscalationMetricAndApprovalSpan()
    {
        _activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        _activities.Tier1Results.Enqueue(() => WorkflowTestData.Tier1Result(AgentDisposition.Escalate, 0.4));
        _activities.Tier2Results.Enqueue(() => WorkflowTestData.Plan(requiresApproval: true));
        _approvalGate.Decisions.Enqueue(new ApprovalDecision(ApprovalOutcome.Approved, "oncall", "Looks safe"));
        _activities.VerificationResults.Enqueue(() => WorkflowTestData.Verification(VerificationOutcome.Passed));

        IncidentWorkflowResult result = await RunAsync();

        Assert.Equal(IncidentWorkflowState.Resolved, result.FinalState);
        Assert.Contains(_spans, s => s.OperationName == SpanNames.Tier2Planning);
        Activity approvalSpan = Assert.Single(_spans, s => s.OperationName == SpanNames.ApprovalWait);
        Assert.Equal("Approved", approvalSpan.GetTagItem(ObservabilityTags.Outcome));
        Assert.Single(_instruments, name => name == "incident_escalated_total");
        Assert.Contains("tier2_duration_seconds", _instruments);
    }

    [Fact]
    public async Task FailedWorkflow_RecordsFailureMetricAndErrorSpanStatus()
    {
        // No evidence results enqueued: evidence collection fails on every attempt.
        IncidentWorkflowResult result = await RunAsync();

        Assert.Equal(IncidentWorkflowState.Failed, result.FinalState);

        Activity workflowSpan = Assert.Single(_spans, s => s.OperationName == SpanNames.WorkflowExecution);
        Assert.Equal("Failed", workflowSpan.GetTagItem(ObservabilityTags.FinalState));
        Assert.Equal(ActivityStatusCode.Error, workflowSpan.Status);

        Assert.Contains("incident_failed_total", _instruments);
        Assert.DoesNotContain("incident_resolved_total", _instruments);
    }
}
