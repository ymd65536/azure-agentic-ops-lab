using AzureAgenticOps.Contracts;

namespace AzureAgenticOps.ScribeService;

/// <summary>
/// A single entry in a post-incident record timeline.
/// </summary>
/// <param name="OccurredAt">When the event occurred.</param>
/// <param name="EventType">The lifecycle event type.</param>
/// <param name="Component">The component that emitted the event.</param>
/// <param name="Outcome">The recorded outcome, when applicable.</param>
public sealed record PostIncidentTimelineEntry(
    DateTimeOffset OccurredAt,
    string EventType,
    string Component,
    string? Outcome);

/// <summary>
/// A deterministic post-incident record draft assembled from structured
/// lifecycle events.
/// </summary>
/// <param name="SchemaVersion">The contract schema version.</param>
/// <param name="IncidentId">The incident the record describes.</param>
/// <param name="FinalState">The last recorded workflow state, when a state change was recorded.</param>
/// <param name="EventCount">The number of unique lifecycle events recorded.</param>
/// <param name="ExecutedActionCount">The number of completed action executions.</param>
/// <param name="ApprovalOutcome">The recorded human approval outcome, when approval occurred.</param>
/// <param name="VerificationOutcome">The last recorded verification outcome, when verification ran.</param>
/// <param name="Timeline">The ordered timeline entries.</param>
/// <param name="GeneratedAt">When the record was generated.</param>
public sealed record PostIncidentRecord(
    string SchemaVersion,
    string IncidentId,
    string? FinalState,
    int EventCount,
    int ExecutedActionCount,
    string? ApprovalOutcome,
    string? VerificationOutcome,
    IReadOnlyList<PostIncidentTimelineEntry> Timeline,
    DateTimeOffset GeneratedAt);

/// <summary>
/// Generates deterministic post-incident record drafts from completed incident
/// timelines. The generator never sits on the critical remediation path and
/// never invents facts: every field is derived directly from recorded events. A
/// model may later turn the structured record into prose, but the structured
/// record itself is authoritative.
/// </summary>
public sealed class PostIncidentRecordGenerator
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new generator.</summary>
    /// <param name="timeProvider">The time provider. Defaults to <see cref="TimeProvider.System"/>.</param>
    public PostIncidentRecordGenerator(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Generates a post-incident record from an ordered incident timeline.
    /// </summary>
    /// <param name="incidentId">The incident the timeline belongs to.</param>
    /// <param name="timeline">The ordered lifecycle events for the incident.</param>
    /// <returns>The deterministic post-incident record draft.</returns>
    public PostIncidentRecord Generate(string incidentId, IReadOnlyList<IncidentLifecycleEvent> timeline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incidentId);
        ArgumentNullException.ThrowIfNull(timeline);

        string? finalState = null;
        int executedActionCount = 0;
        string? approvalOutcome = null;
        string? verificationOutcome = null;

        foreach (IncidentLifecycleEvent lifecycleEvent in timeline)
        {
            switch (lifecycleEvent.EventType)
            {
                case "StateChanged":
                    finalState = lifecycleEvent.Outcome;
                    break;
                case "ExecutionCompleted":
                    executedActionCount++;
                    break;
                case "ApprovalCompleted":
                    approvalOutcome = lifecycleEvent.Outcome;
                    break;
                case "VerificationCompleted":
                    verificationOutcome = lifecycleEvent.Outcome;
                    break;
                default:
                    break;
            }
        }

        var entries = timeline
            .Select(item => new PostIncidentTimelineEntry(
                item.OccurredAt,
                item.EventType,
                item.Component,
                item.Outcome))
            .ToArray();

        return new PostIncidentRecord(
            SchemaVersions.V1,
            incidentId,
            finalState,
            timeline.Count,
            executedActionCount,
            approvalOutcome,
            verificationOutcome,
            entries,
            _timeProvider.GetUtcNow());
    }
}
