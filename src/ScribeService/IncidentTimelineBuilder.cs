using AzureAgenticOps.Contracts;

namespace AzureAgenticOps.ScribeService;

/// <summary>
/// Builds ordered incident timelines from lifecycle events. The builder is an
/// asynchronous Pub/Sub consumer concern: it tolerates duplicate delivery by
/// deduplicating on event ID and tolerates out-of-order delivery by sorting on
/// the event occurrence time. Construction is fully deterministic; no model is
/// involved.
/// </summary>
public sealed class IncidentTimelineBuilder
{
    private readonly Dictionary<string, Dictionary<string, IncidentLifecycleEvent>> _eventsByIncident =
        new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>
    /// Records a lifecycle event. Duplicate events (same event ID) are ignored.
    /// </summary>
    /// <param name="lifecycleEvent">The event to record.</param>
    /// <returns><see langword="true"/> when the event was new; <see langword="false"/> for a duplicate.</returns>
    public bool Record(IncidentLifecycleEvent lifecycleEvent)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);

        lock (_lock)
        {
            if (!_eventsByIncident.TryGetValue(lifecycleEvent.IncidentId, out Dictionary<string, IncidentLifecycleEvent>? events))
            {
                events = new Dictionary<string, IncidentLifecycleEvent>(StringComparer.Ordinal);
                _eventsByIncident[lifecycleEvent.IncidentId] = events;
            }

            return events.TryAdd(lifecycleEvent.EventId, lifecycleEvent);
        }
    }

    /// <summary>
    /// Builds the ordered timeline for an incident. Events are ordered by
    /// occurrence time; ties keep a stable order by event ID.
    /// </summary>
    /// <param name="incidentId">The incident to build the timeline for.</param>
    /// <returns>The ordered lifecycle events, or an empty list when none were recorded.</returns>
    public IReadOnlyList<IncidentLifecycleEvent> BuildTimeline(string incidentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incidentId);

        lock (_lock)
        {
            if (!_eventsByIncident.TryGetValue(incidentId, out Dictionary<string, IncidentLifecycleEvent>? events))
            {
                return [];
            }

            return [.. events.Values
                .OrderBy(item => item.OccurredAt)
                .ThenBy(item => item.EventId, StringComparer.Ordinal)];
        }
    }
}
