using System.Collections.Concurrent;
using AzureAgenticOps.Contracts;
using AzureAgenticOps.IncidentWorkflow;

namespace AzureAgenticOps.IncidentApi;

/// <summary>
/// Options for the in-memory lifecycle timeline. The timeline exists so the
/// operations console can visualize a run; it is bounded so a long-lived host
/// cannot grow without limit and is never part of the remediation path.
/// </summary>
public sealed class IncidentTimelineOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "IncidentTimeline";

    /// <summary>Gets or sets the maximum number of events retained per incident.</summary>
    public int MaxEventsPerIncident { get; set; } = 200;

    /// <summary>Gets or sets the maximum number of incidents retained.</summary>
    public int MaxIncidents { get; set; } = 100;
}

/// <summary>
/// Records incident lifecycle events in memory, in arrival order, so the
/// operations console can render an ordered timeline of a workflow run.
/// Recording never fails the publishing path: the recorder only appends to a
/// bounded buffer.
/// </summary>
public sealed class IncidentTimelineRecorder : ILifecycleEventPublisher
{
    private readonly ConcurrentDictionary<string, Queue<IncidentLifecycleEvent>> _eventsByIncident =
        new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _incidentOrder = new();
    private readonly IncidentTimelineOptions _options;

    /// <summary>Initializes a new recorder.</summary>
    /// <param name="options">The timeline retention options.</param>
    public IncidentTimelineRecorder(IncidentTimelineOptions? options = null)
    {
        _options = options ?? new IncidentTimelineOptions();
    }

    /// <summary>Gets the recorded events for an incident, in arrival order.</summary>
    /// <param name="incidentId">The incident identifier.</param>
    /// <returns>The recorded events, or an empty list when none were recorded.</returns>
    public IReadOnlyList<IncidentLifecycleEvent> GetEvents(string incidentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incidentId);
        if (!_eventsByIncident.TryGetValue(incidentId, out Queue<IncidentLifecycleEvent>? events))
        {
            return [];
        }

        lock (events)
        {
            return [.. events];
        }
    }

    /// <inheritdoc />
    public Task PublishAsync(IncidentLifecycleEvent lifecycleEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);

        Queue<IncidentLifecycleEvent> events = _eventsByIncident.GetOrAdd(
            lifecycleEvent.IncidentId,
            incidentId =>
            {
                _incidentOrder.Enqueue(incidentId);
                return new Queue<IncidentLifecycleEvent>();
            });

        lock (events)
        {
            events.Enqueue(lifecycleEvent);
            while (events.Count > _options.MaxEventsPerIncident)
            {
                events.Dequeue();
            }
        }

        TrimIncidents();
        return Task.CompletedTask;
    }

    private void TrimIncidents()
    {
        while (_eventsByIncident.Count > _options.MaxIncidents &&
               _incidentOrder.TryDequeue(out string? oldestIncidentId))
        {
            _eventsByIncident.TryRemove(oldestIncidentId, out _);
        }
    }
}
