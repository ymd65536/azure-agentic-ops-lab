using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AzureAgenticOps.Observability;

/// <summary>
/// The metrics recommended by the observability requirements, emitted through a
/// <see cref="Meter"/> named <see cref="ObservabilityNames.MeterName"/>. All
/// labels are low cardinality; incident identifiers and other high-cardinality
/// values must never be passed as metric labels and belong in traces and logs.
/// </summary>
public sealed class AgenticOpsMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly Counter<long> _incidentTotal;
    private readonly Counter<long> _incidentResolvedTotal;
    private readonly Counter<long> _incidentFailedTotal;
    private readonly Counter<long> _incidentEscalatedTotal;
    private readonly Histogram<double> _incidentDurationSeconds;
    private readonly Histogram<double> _tier1DurationSeconds;
    private readonly Histogram<double> _tier2DurationSeconds;
    private readonly Counter<long> _agentModelRequestTotal;
    private readonly Counter<long> _agentModelFailureTotal;
    private readonly Counter<long> _agentModelInputTokens;
    private readonly Counter<long> _agentModelOutputTokens;
    private readonly Counter<long> _toolInvocationTotal;
    private readonly Counter<long> _actionExecutionTotal;
    private readonly Counter<long> _actionRejectedTotal;
    private readonly Counter<long> _workflowResumeTotal;
    private readonly Counter<long> _duplicateEventTotal;
    private readonly Counter<long> _verificationFailureTotal;

    /// <summary>Initializes the metric instruments.</summary>
    /// <param name="meterFactory">
    /// An optional factory from the host's dependency injection container. When
    /// <c>null</c>, a standalone meter is created, which keeps the type usable
    /// in tests and plain library hosts.
    /// </param>
    public AgenticOpsMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory is null
            ? new Meter(ObservabilityNames.MeterName)
            : meterFactory.Create(ObservabilityNames.MeterName);

        _incidentTotal = _meter.CreateCounter<long>("incident_total", description: "Incidents received.");
        _incidentResolvedTotal = _meter.CreateCounter<long>("incident_resolved_total", description: "Incidents that reached the Resolved state.");
        _incidentFailedTotal = _meter.CreateCounter<long>("incident_failed_total", description: "Incidents that reached a non-resolved terminal state.");
        _incidentEscalatedTotal = _meter.CreateCounter<long>("incident_escalated_total", description: "Incidents escalated to Tier 2.");
        _incidentDurationSeconds = _meter.CreateHistogram<double>("incident_duration_seconds", unit: "s", description: "End-to-end incident workflow duration.");
        _tier1DurationSeconds = _meter.CreateHistogram<double>("tier1_duration_seconds", unit: "s", description: "Tier 1 investigation activity duration.");
        _tier2DurationSeconds = _meter.CreateHistogram<double>("tier2_duration_seconds", unit: "s", description: "Tier 2 planning activity duration.");
        _agentModelRequestTotal = _meter.CreateCounter<long>("agent_model_request_total", description: "Model invocations.");
        _agentModelFailureTotal = _meter.CreateCounter<long>("agent_model_failure_total", description: "Model invocations that failed or produced invalid output.");
        _agentModelInputTokens = _meter.CreateCounter<long>("agent_model_input_tokens", description: "Input tokens consumed by model invocations.");
        _agentModelOutputTokens = _meter.CreateCounter<long>("agent_model_output_tokens", description: "Output tokens produced by model invocations.");
        _toolInvocationTotal = _meter.CreateCounter<long>("tool_invocation_total", description: "Agent tool invocations.");
        _actionExecutionTotal = _meter.CreateCounter<long>("action_execution_total", description: "Remediation action execution attempts by outcome.");
        _actionRejectedTotal = _meter.CreateCounter<long>("action_rejected_total", description: "Remediation actions rejected by policy.");
        _workflowResumeTotal = _meter.CreateCounter<long>("workflow_resume_total", description: "Workflow resumptions after a process restart.");
        _duplicateEventTotal = _meter.CreateCounter<long>("duplicate_event_total", description: "Duplicate deliveries detected by idempotent consumers.");
        _verificationFailureTotal = _meter.CreateCounter<long>("verification_failure_total", description: "Verification attempts that did not pass.");
    }

    /// <summary>Records that an incident was received.</summary>
    public void RecordIncidentReceived() => _incidentTotal.Add(1);

    /// <summary>Records a resolved incident and its end-to-end duration.</summary>
    /// <param name="duration">The end-to-end workflow duration.</param>
    public void RecordIncidentResolved(TimeSpan duration)
    {
        _incidentResolvedTotal.Add(1);
        _incidentDurationSeconds.Record(duration.TotalSeconds, new KeyValuePair<string, object?>(ObservabilityTags.FinalState, "Resolved"));
    }

    /// <summary>Records an incident that terminated without resolution.</summary>
    /// <param name="finalState">The low-cardinality terminal state name.</param>
    /// <param name="duration">The end-to-end workflow duration.</param>
    public void RecordIncidentFailed(string finalState, TimeSpan duration)
    {
        _incidentFailedTotal.Add(1, new KeyValuePair<string, object?>(ObservabilityTags.FinalState, finalState));
        _incidentDurationSeconds.Record(duration.TotalSeconds, new KeyValuePair<string, object?>(ObservabilityTags.FinalState, finalState));
    }

    /// <summary>Records that an incident was escalated to Tier 2.</summary>
    public void RecordIncidentEscalated() => _incidentEscalatedTotal.Add(1);

    /// <summary>Records the duration of a Tier 1 investigation attempt.</summary>
    /// <param name="duration">The activity duration.</param>
    public void RecordTier1Duration(TimeSpan duration) => _tier1DurationSeconds.Record(duration.TotalSeconds);

    /// <summary>Records the duration of a Tier 2 planning attempt.</summary>
    /// <param name="duration">The activity duration.</param>
    public void RecordTier2Duration(TimeSpan duration) => _tier2DurationSeconds.Record(duration.TotalSeconds);

    /// <summary>Records a model invocation with token usage when available.</summary>
    /// <param name="promptName">The version-controlled prompt name.</param>
    /// <param name="modelId">The model identifier.</param>
    /// <param name="succeeded">Whether the invocation produced validated structured output.</param>
    /// <param name="inputTokens">Input tokens, when reported.</param>
    /// <param name="outputTokens">Output tokens, when reported.</param>
    public void RecordModelInvocation(string promptName, string modelId, bool succeeded, int? inputTokens, int? outputTokens)
    {
        var tags = new TagList
        {
            { ObservabilityTags.PromptName, promptName },
            { ObservabilityTags.ModelId, modelId },
        };
        _agentModelRequestTotal.Add(1, tags);
        if (!succeeded)
        {
            _agentModelFailureTotal.Add(1, tags);
        }

        if (inputTokens is not null)
        {
            _agentModelInputTokens.Add(inputTokens.Value, tags);
        }

        if (outputTokens is not null)
        {
            _agentModelOutputTokens.Add(outputTokens.Value, tags);
        }
    }

    /// <summary>Records an agent tool invocation.</summary>
    /// <param name="toolName">The low-cardinality tool name.</param>
    /// <param name="outcome">The low-cardinality outcome value.</param>
    public void RecordToolInvocation(string toolName, string outcome) =>
        _toolInvocationTotal.Add(1, new TagList
        {
            { ObservabilityTags.ToolName, toolName },
            { ObservabilityTags.Outcome, outcome },
        });

    /// <summary>Records a remediation action execution attempt.</summary>
    /// <param name="actionType">The structured action type.</param>
    /// <param name="outcome">The low-cardinality outcome value.</param>
    public void RecordActionExecution(string actionType, string outcome)
    {
        _actionExecutionTotal.Add(1, new TagList
        {
            { ObservabilityTags.ActionType, actionType },
            { ObservabilityTags.Outcome, outcome },
        });
        if (string.Equals(outcome, "Rejected", StringComparison.OrdinalIgnoreCase))
        {
            _actionRejectedTotal.Add(1, new KeyValuePair<string, object?>(ObservabilityTags.ActionType, actionType));
        }
    }

    /// <summary>Records a workflow resumption after a process restart.</summary>
    public void RecordWorkflowResume() => _workflowResumeTotal.Add(1);

    /// <summary>Records a duplicate delivery detected by an idempotent consumer.</summary>
    /// <param name="component">The consumer component name.</param>
    public void RecordDuplicateEvent(string component) =>
        _duplicateEventTotal.Add(1, new KeyValuePair<string, object?>(ObservabilityTags.Component, component));

    /// <summary>Records a verification attempt that did not pass.</summary>
    /// <param name="outcome">The low-cardinality verification outcome.</param>
    public void RecordVerificationFailure(string outcome) =>
        _verificationFailureTotal.Add(1, new KeyValuePair<string, object?>(ObservabilityTags.Outcome, outcome));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
