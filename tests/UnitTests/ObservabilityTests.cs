using System.Diagnostics;
using System.Diagnostics.Metrics;
using AzureAgenticOps.Observability;

namespace AzureAgenticOps.UnitTests;

public sealed class ObservabilityTests
{
    private static ActivityListener CreateListener(List<Activity> completed)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ObservabilityNames.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = completed.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [Fact]
    public void StartSpan_AttachesRequiredCorrelationTags()
    {
        var completed = new List<Activity>();
        using ActivityListener listener = CreateListener(completed);
        var context = new CorrelationContext("inc-001", "corr-001", "TestComponent", "wf-001");

        using (Activity? span = AgenticOpsActivitySource.StartSpan(SpanNames.Tier1Investigation, context, attemptNumber: 2))
        {
            Assert.NotNull(span);
            AgenticOpsActivitySource.RecordSuccess(span, "success");
        }

        Activity activity = Assert.Single(completed);
        Assert.Equal(SpanNames.Tier1Investigation, activity.OperationName);
        Assert.Equal("inc-001", activity.GetTagItem(ObservabilityTags.IncidentId));
        Assert.Equal("corr-001", activity.GetTagItem(ObservabilityTags.CorrelationId));
        Assert.Equal("TestComponent", activity.GetTagItem(ObservabilityTags.Component));
        Assert.Equal("wf-001", activity.GetTagItem(ObservabilityTags.WorkflowInstanceId));
        Assert.Equal(2, activity.GetTagItem(ObservabilityTags.AttemptNumber));
        Assert.Equal("success", activity.GetTagItem(ObservabilityTags.Outcome));
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
    }

    [Fact]
    public void RecordFailure_SetsErrorStatusAndCategory()
    {
        var completed = new List<Activity>();
        using ActivityListener listener = CreateListener(completed);
        var context = new CorrelationContext("inc-001", "corr-001", "TestComponent");

        using (Activity? span = AgenticOpsActivitySource.StartSpan(SpanNames.Execution, context))
        {
            AgenticOpsActivitySource.RecordFailure(span, "TimeoutException");
        }

        Activity activity = Assert.Single(completed);
        Assert.Equal("error", activity.GetTagItem(ObservabilityTags.Outcome));
        Assert.Equal("TimeoutException", activity.GetTagItem(ObservabilityTags.ErrorCategory));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
    }

    [Fact]
    public void StartSpan_WithoutListener_ReturnsNullSafely()
    {
        var context = new CorrelationContext("inc-001", "corr-001", "TestComponent");

        Activity? span = AgenticOpsActivitySource.StartSpan("unlistened.span.name", context);

        AgenticOpsActivitySource.RecordSuccess(span, "success");
        AgenticOpsActivitySource.RecordFailure(span, "None");
        span?.Dispose();
    }

    [Fact]
    public void Metrics_EmitRecommendedInstrumentsWithLowCardinalityTags()
    {
        var measurements = new List<(string Instrument, double Value, Dictionary<string, object?> Tags)>();
        using var metrics = new AgenticOpsMetrics();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ObservabilityNames.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();

        metrics.RecordIncidentReceived();
        metrics.RecordIncidentEscalated();
        metrics.RecordIncidentResolved(TimeSpan.FromSeconds(30));
        metrics.RecordIncidentFailed("Terminated", TimeSpan.FromSeconds(45));
        metrics.RecordTier1Duration(TimeSpan.FromSeconds(2));
        metrics.RecordTier2Duration(TimeSpan.FromSeconds(5));
        metrics.RecordModelInvocation("tier1-investigation", "fake-model", succeeded: false, inputTokens: 100, outputTokens: 50);
        metrics.RecordToolInvocation("insights_search", "success");
        metrics.RecordActionExecution("restart_deployment", "Rejected");
        metrics.RecordWorkflowResume();
        metrics.RecordDuplicateEvent("ScribeService");
        metrics.RecordVerificationFailure("Inconclusive");

        Assert.Contains(measurements, m => m.Instrument == "incident_total" && m.Value == 1);
        Assert.Contains(measurements, m => m.Instrument == "incident_escalated_total");
        Assert.Contains(measurements, m => m.Instrument == "incident_resolved_total");
        Assert.Contains(measurements, m =>
            m.Instrument == "incident_failed_total" &&
            Equals(m.Tags[ObservabilityTags.FinalState], "Terminated"));
        Assert.Equal(2, measurements.Count(m => m.Instrument == "incident_duration_seconds"));
        Assert.Contains(measurements, m => m.Instrument == "tier1_duration_seconds" && m.Value == 2);
        Assert.Contains(measurements, m => m.Instrument == "tier2_duration_seconds" && m.Value == 5);
        Assert.Contains(measurements, m =>
            m.Instrument == "agent_model_request_total" &&
            Equals(m.Tags[ObservabilityTags.PromptName], "tier1-investigation") &&
            Equals(m.Tags[ObservabilityTags.ModelId], "fake-model"));
        Assert.Contains(measurements, m => m.Instrument == "agent_model_failure_total");
        Assert.Contains(measurements, m => m.Instrument == "agent_model_input_tokens" && m.Value == 100);
        Assert.Contains(measurements, m => m.Instrument == "agent_model_output_tokens" && m.Value == 50);
        Assert.Contains(measurements, m => m.Instrument == "tool_invocation_total");
        Assert.Contains(measurements, m =>
            m.Instrument == "action_execution_total" &&
            Equals(m.Tags[ObservabilityTags.ActionType], "restart_deployment"));
        Assert.Contains(measurements, m => m.Instrument == "action_rejected_total");
        Assert.Contains(measurements, m => m.Instrument == "workflow_resume_total");
        Assert.Contains(measurements, m =>
            m.Instrument == "duplicate_event_total" &&
            Equals(m.Tags[ObservabilityTags.Component], "ScribeService"));
        Assert.Contains(measurements, m =>
            m.Instrument == "verification_failure_total" &&
            Equals(m.Tags[ObservabilityTags.Outcome], "Inconclusive"));

        // Incident identifiers are high cardinality and must never appear as metric labels.
        Assert.DoesNotContain(measurements, m => m.Tags.ContainsKey(ObservabilityTags.IncidentId));
    }

    [Fact]
    public void Metrics_SuccessfulModelInvocation_DoesNotCountAsFailure()
    {
        var instruments = new List<string>();
        using var metrics = new AgenticOpsMetrics();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ObservabilityNames.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) => instruments.Add(instrument.Name));
        listener.Start();

        metrics.RecordModelInvocation("tier2-remediation", "fake-model", succeeded: true, inputTokens: null, outputTokens: null);
        metrics.RecordActionExecution("scale_deployment", "Succeeded");

        Assert.Contains("agent_model_request_total", instruments);
        Assert.DoesNotContain("agent_model_failure_total", instruments);
        Assert.DoesNotContain("agent_model_input_tokens", instruments);
        Assert.DoesNotContain("action_rejected_total", instruments);
    }
}
