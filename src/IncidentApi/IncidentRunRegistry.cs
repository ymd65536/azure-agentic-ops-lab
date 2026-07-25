using System.Collections.Concurrent;
using AzureAgenticOps.Contracts;
using AzureAgenticOps.IncidentWorkflow;

namespace AzureAgenticOps.IncidentApi;

/// <summary>
/// The observable status of one incident workflow run.
/// </summary>
/// <param name="IncidentId">The incident being processed.</param>
/// <param name="WorkflowInstanceId">The workflow instance identifier.</param>
/// <param name="CorrelationId">The correlation identifier for all related operations.</param>
/// <param name="CurrentState">The most recently observed workflow state.</param>
/// <param name="IsCompleted">Whether the workflow reached a terminal state.</param>
/// <param name="Result">The final workflow result, when completed.</param>
/// <param name="Title">The incident title, for display.</param>
/// <param name="Severity">The reported incident severity, for display.</param>
/// <param name="StartedAt">When the run was started.</param>
public sealed record IncidentRunStatus(
    string IncidentId,
    string WorkflowInstanceId,
    string CorrelationId,
    IncidentWorkflowState CurrentState,
    bool IsCompleted,
    IncidentWorkflowResult? Result,
    string? Title = null,
    string? Severity = null,
    DateTimeOffset? StartedAt = null);

/// <summary>
/// Starts incident workflow runs as supervised background tasks and tracks their
/// status. One run is allowed per incident identifier; duplicate submissions are
/// rejected so that duplicate delivery cannot start a second remediation.
/// </summary>
public sealed class IncidentRunRegistry
{
    private sealed class RunEntry
    {
        public required string IncidentId { get; init; }

        public required string WorkflowInstanceId { get; init; }

        public required string CorrelationId { get; init; }

        public required string Title { get; init; }

        public required string Severity { get; init; }

        public required DateTimeOffset StartedAt { get; init; }

        public volatile IncidentWorkflowState CurrentState = IncidentWorkflowState.Received;

        public IncidentWorkflowResult? Result;
    }

    private readonly ConcurrentDictionary<string, RunEntry> _runs = new(StringComparer.Ordinal);
    private readonly IIncidentWorkflowRunner _runner;
    private readonly WorkflowStateObserver _stateObserver;
    private readonly ILogger<IncidentRunRegistry> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new registry.</summary>
    /// <param name="runner">The workflow runner for the configured hosting engine.</param>
    /// <param name="stateObserver">The observer tracking in-flight workflow states.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="lifetime">The host lifetime used to stop runs on shutdown.</param>
    /// <param name="timeProvider">The time provider used to stamp run start times.</param>
    public IncidentRunRegistry(
        IIncidentWorkflowRunner runner,
        WorkflowStateObserver stateObserver,
        ILogger<IncidentRunRegistry> logger,
        IHostApplicationLifetime lifetime,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(stateObserver);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _runner = runner;
        _stateObserver = stateObserver;
        _logger = logger;
        _lifetime = lifetime;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Starts a workflow run for an incident. Returns <see langword="null"/> when a
    /// run for the same incident already exists.
    /// </summary>
    /// <param name="incident">The submitted incident.</param>
    /// <returns>The initial run status, or <see langword="null"/> for duplicates.</returns>
    public IncidentRunStatus? TryStartRun(Incident incident)
    {
        ArgumentNullException.ThrowIfNull(incident);

        var entry = new RunEntry
        {
            IncidentId = incident.IncidentId,
            WorkflowInstanceId = $"wf-{Guid.NewGuid():N}",
            CorrelationId = $"corr-{Guid.NewGuid():N}",
            Title = incident.Title,
            Severity = incident.Severity,
            StartedAt = _timeProvider.GetUtcNow(),
        };

        if (!_runs.TryAdd(incident.IncidentId, entry))
        {
            return null;
        }

        CancellationToken stoppingToken = _lifetime.ApplicationStopping;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    IncidentWorkflowResult result = await _runner
                        .RunAsync(incident, entry.WorkflowInstanceId, entry.CorrelationId, stoppingToken)
                        .ConfigureAwait(false);
                    Volatile.Write(ref entry.Result, result);
                    entry.CurrentState = result.FinalState;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "Workflow {WorkflowInstanceId} for incident {IncidentId} was cancelled by host shutdown.",
                        entry.WorkflowInstanceId, entry.IncidentId);
                }
                catch (Exception exception)
                {
                    // The orchestrator is designed to terminate safely; an escaped
                    // exception is an infrastructure failure and is recorded as such.
                    _logger.LogError(
                        exception,
                        "Workflow {WorkflowInstanceId} for incident {IncidentId} failed unexpectedly.",
                        entry.WorkflowInstanceId, entry.IncidentId);
                    entry.CurrentState = IncidentWorkflowState.Failed;
                }
            },
            CancellationToken.None);

        return ToStatus(entry);
    }

    /// <summary>Gets the status of every known run, newest first.</summary>
    /// <returns>The status of all tracked runs.</returns>
    public IReadOnlyList<IncidentRunStatus> GetStatuses() =>
        [.. _runs.Values
            .OrderByDescending(entry => entry.StartedAt)
            .ThenBy(entry => entry.IncidentId, StringComparer.Ordinal)
            .Select(ToStatus)];

    /// <summary>Gets the status of a run.</summary>
    /// <param name="incidentId">The incident identifier.</param>
    /// <returns>The status, or <see langword="null"/> when no run exists.</returns>
    public IncidentRunStatus? GetStatus(string incidentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incidentId);
        return _runs.TryGetValue(incidentId, out RunEntry? entry) ? ToStatus(entry) : null;
    }

    private IncidentRunStatus ToStatus(RunEntry entry)
    {
        IncidentWorkflowResult? result = Volatile.Read(ref entry.Result);
        IncidentWorkflowState currentState = result?.FinalState
            ?? (_stateObserver.TryGetState(entry.IncidentId, out IncidentWorkflowState observed)
                ? observed
                : entry.CurrentState);
        return new IncidentRunStatus(
            entry.IncidentId,
            entry.WorkflowInstanceId,
            entry.CorrelationId,
            currentState,
            result is not null,
            result,
            entry.Title,
            entry.Severity,
            entry.StartedAt);
    }
}
