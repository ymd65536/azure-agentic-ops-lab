using System.Diagnostics;

namespace AzureAgenticOps.Observability;

/// <summary>
/// Correlation data that must accompany every workflow transition, agent
/// request, tool invocation, execution, and verification so that operations can
/// be traced end to end.
/// </summary>
/// <param name="IncidentId">The incident identifier.</param>
/// <param name="CorrelationId">The correlation identifier shared across all operations for the incident.</param>
/// <param name="Component">The name of the emitting component.</param>
/// <param name="WorkflowInstanceId">The workflow instance identifier, when the operation runs inside a workflow.</param>
public sealed record CorrelationContext(
    string IncidentId,
    string CorrelationId,
    string Component,
    string? WorkflowInstanceId = null);

/// <summary>
/// The shared <see cref="ActivitySource"/> for all spans in the system, plus
/// helpers that guarantee every span carries the required correlation tags.
/// Hosts subscribe the OpenTelemetry SDK to
/// <see cref="ObservabilityNames.ActivitySourceName"/> to export the spans.
/// </summary>
public static class AgenticOpsActivitySource
{
    /// <summary>The shared activity source. Immutable and safe to use from any component.</summary>
    public static ActivitySource Instance { get; } = new(ObservabilityNames.ActivitySourceName);

    /// <summary>
    /// Starts a span with the required correlation tags. Returns <c>null</c>
    /// when no listener is registered, which callers must tolerate.
    /// </summary>
    /// <param name="spanName">The stable span name, typically from <see cref="SpanNames"/>.</param>
    /// <param name="context">The correlation context for the operation.</param>
    /// <param name="attemptNumber">The attempt number when the operation is retried.</param>
    /// <returns>The started activity, or <c>null</c> when sampling excludes the span.</returns>
    public static Activity? StartSpan(string spanName, CorrelationContext context, int? attemptNumber = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spanName);
        ArgumentNullException.ThrowIfNull(context);

        Activity? activity = Instance.StartActivity(spanName, ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(ObservabilityTags.IncidentId, context.IncidentId);
        activity.SetTag(ObservabilityTags.CorrelationId, context.CorrelationId);
        activity.SetTag(ObservabilityTags.Component, context.Component);
        if (context.WorkflowInstanceId is not null)
        {
            activity.SetTag(ObservabilityTags.WorkflowInstanceId, context.WorkflowInstanceId);
        }

        if (attemptNumber is not null)
        {
            activity.SetTag(ObservabilityTags.AttemptNumber, attemptNumber.Value);
        }

        return activity;
    }

    /// <summary>Records a successful outcome on a span.</summary>
    /// <param name="activity">The span to complete. Ignored when <c>null</c>.</param>
    /// <param name="outcome">The low-cardinality outcome value.</param>
    public static void RecordSuccess(Activity? activity, string outcome)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(ObservabilityTags.Outcome, outcome);
        activity.SetStatus(ActivityStatusCode.Ok);
    }

    /// <summary>Records a failure outcome and error category on a span.</summary>
    /// <param name="activity">The span to complete. Ignored when <c>null</c>.</param>
    /// <param name="errorCategory">The low-cardinality error category, such as an exception type name.</param>
    public static void RecordFailure(Activity? activity, string errorCategory)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(ObservabilityTags.Outcome, "error");
        activity.SetTag(ObservabilityTags.ErrorCategory, errorCategory);
        activity.SetStatus(ActivityStatusCode.Error, errorCategory);
    }
}
