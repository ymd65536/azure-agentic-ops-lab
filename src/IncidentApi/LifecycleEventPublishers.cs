using System.Text;
using AzureAgenticOps.Contracts;
using AzureAgenticOps.IncidentWorkflow;
using Microsoft.Extensions.Options;

namespace AzureAgenticOps.IncidentApi;

/// <summary>
/// Options for the Dapr lifecycle event publisher. The logical component and
/// topic names stay stable across environments.
/// </summary>
public sealed class DaprPublisherOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Dapr";

    /// <summary>Gets or sets whether publishing through the Dapr sidecar is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the logical Pub/Sub component name.</summary>
    public string PubSubName { get; set; } = "incident-pubsub";

    /// <summary>Gets or sets the lifecycle event topic name.</summary>
    public string TopicName { get; set; } = "incident-lifecycle";

    /// <summary>Gets or sets the Dapr sidecar HTTP port.</summary>
    public int HttpPort { get; set; } = 3500;
}

/// <summary>
/// Publishes lifecycle events to the Dapr Pub/Sub building block through the
/// sidecar HTTP API. Publishing failures are logged and never propagated, so an
/// unavailable sidecar cannot block the remediation path. When disabled, the
/// publisher is a logged no-op so the host also runs without Dapr.
/// </summary>
public sealed class DaprLifecycleEventPublisher : ILifecycleEventPublisher
{
    private readonly HttpClient _httpClient;
    private readonly DaprPublisherOptions _options;
    private readonly ILogger<DaprLifecycleEventPublisher> _logger;

    /// <summary>Initializes a new publisher.</summary>
    /// <param name="httpClient">The HTTP client used to reach the sidecar.</param>
    /// <param name="options">The publisher options.</param>
    /// <param name="logger">The logger.</param>
    public DaprLifecycleEventPublisher(
        HttpClient httpClient,
        IOptions<DaprPublisherOptions> options,
        ILogger<DaprLifecycleEventPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task PublishAsync(IncidentLifecycleEvent lifecycleEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);

        if (!_options.Enabled)
        {
            return;
        }

        var requestUri = new Uri(
            $"http://127.0.0.1:{_options.HttpPort}/v1.0/publish/{Uri.EscapeDataString(_options.PubSubName)}/{Uri.EscapeDataString(_options.TopicName)}");

        try
        {
            using var content = new StringContent(
                ContractSerialization.Serialize(lifecycleEvent),
                Encoding.UTF8,
                "application/json");
            using HttpResponseMessage response = await _httpClient
                .PostAsync(requestUri, content, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Dapr publish of lifecycle event {EventType} for incident {IncidentId} returned status {StatusCode}.",
                    lifecycleEvent.EventType, lifecycleEvent.IncidentId, (int)response.StatusCode);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(
                exception,
                "Dapr publish of lifecycle event {EventType} for incident {IncidentId} failed.",
                lifecycleEvent.EventType, lifecycleEvent.IncidentId);
        }
    }
}

/// <summary>
/// Fans one lifecycle event out to several publishers. A failing publisher never
/// prevents the remaining publishers from observing the event.
/// </summary>
public sealed class CompositeLifecycleEventPublisher : ILifecycleEventPublisher
{
    private readonly IReadOnlyList<ILifecycleEventPublisher> _publishers;

    /// <summary>Initializes a new composite publisher.</summary>
    /// <param name="publishers">The publishers to fan out to, in order.</param>
    public CompositeLifecycleEventPublisher(IReadOnlyList<ILifecycleEventPublisher> publishers)
    {
        ArgumentNullException.ThrowIfNull(publishers);
        _publishers = publishers;
    }

    /// <inheritdoc />
    public async Task PublishAsync(IncidentLifecycleEvent lifecycleEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);

        List<Exception>? failures = null;
        foreach (ILifecycleEventPublisher publisher in _publishers)
        {
            try
            {
                await publisher.PublishAsync(lifecycleEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more lifecycle publishers failed.", failures);
        }
    }
}

/// <summary>
/// A lifecycle publisher that records the latest observed workflow state per
/// incident so status queries can report progress while a run is in flight.
/// </summary>
public sealed class WorkflowStateObserver : ILifecycleEventPublisher
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IncidentWorkflowState> _statesByIncident =
        new(StringComparer.Ordinal);

    /// <summary>Gets the last observed state for an incident.</summary>
    /// <param name="incidentId">The incident identifier.</param>
    /// <param name="state">The last observed state, when one was recorded.</param>
    /// <returns>Whether a state was observed for the incident.</returns>
    public bool TryGetState(string incidentId, out IncidentWorkflowState state) =>
        _statesByIncident.TryGetValue(incidentId, out state);

    /// <inheritdoc />
    public Task PublishAsync(IncidentLifecycleEvent lifecycleEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lifecycleEvent);
        if (lifecycleEvent.EventType == "StateChanged" &&
            lifecycleEvent.Details is not null &&
            lifecycleEvent.Details.TryGetValue("to", out string? toState) &&
            Enum.TryParse(toState, ignoreCase: true, out IncidentWorkflowState state))
        {
            _statesByIncident[lifecycleEvent.IncidentId] = state;
        }

        return Task.CompletedTask;
    }
}
