using AzureAgenticOps.Contracts;

namespace AzureAgenticOps.IncidentWorkflow;

/// <summary>
/// Publishes incident lifecycle events to the audit and Scribe consumers. In the
/// Dapr-hosted deployment the implementation publishes to the
/// <c>incident-pubsub</c> component. Publisher failures must never block the
/// remediation path; the orchestrator swallows and records publish failures.
/// </summary>
public interface ILifecycleEventPublisher
{
    /// <summary>Publishes a single lifecycle event.</summary>
    /// <param name="lifecycleEvent">The event to publish.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task PublishAsync(IncidentLifecycleEvent lifecycleEvent, CancellationToken cancellationToken);
}

/// <summary>
/// An in-memory lifecycle event publisher for tests and the local demo
/// environment. Events are retained in publish order.
/// </summary>
public sealed class InMemoryLifecycleEventPublisher : ILifecycleEventPublisher
{
    private readonly List<IncidentLifecycleEvent> _events = [];
    private readonly Lock _lock = new();

    /// <summary>Gets a snapshot of all published events in publish order.</summary>
    public IReadOnlyList<IncidentLifecycleEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return [.. _events];
            }
        }
    }

    /// <inheritdoc />
    public Task PublishAsync(IncidentLifecycleEvent lifecycleEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _events.Add(lifecycleEvent);
        }

        return Task.CompletedTask;
    }
}
