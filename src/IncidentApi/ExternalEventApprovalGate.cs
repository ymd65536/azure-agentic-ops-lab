using System.Collections.Concurrent;
using AzureAgenticOps.Contracts;
using AzureAgenticOps.IncidentWorkflow;

namespace AzureAgenticOps.IncidentApi;

/// <summary>
/// An approval gate whose decisions arrive as external HTTP events. The gate
/// never holds an HTTP request open: the workflow awaits an in-process
/// completion source with a bounded timeout while the approval endpoint
/// completes it. Decisions that arrive before the workflow starts waiting are
/// buffered, and decisions that arrive after the timeout are ignored.
/// </summary>
public sealed class ExternalEventApprovalGate : IApprovalGate
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ApprovalDecision>> _pendingWaits =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, ApprovalDecision> _bufferedDecisions =
        new(StringComparer.Ordinal);

    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new gate.</summary>
    /// <param name="timeProvider">The time provider. Defaults to <see cref="TimeProvider.System"/>.</param>
    public ExternalEventApprovalGate(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Delivers an approval decision for an incident. Returns <see langword="false"/>
    /// when a decision was already delivered and is still pending consumption.
    /// </summary>
    /// <param name="incidentId">The incident awaiting approval.</param>
    /// <param name="decision">The human decision.</param>
    /// <returns>Whether the decision was accepted.</returns>
    public bool TryDeliver(string incidentId, ApprovalDecision decision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incidentId);
        ArgumentNullException.ThrowIfNull(decision);

        if (_pendingWaits.TryRemove(incidentId, out TaskCompletionSource<ApprovalDecision>? waiter))
        {
            return waiter.TrySetResult(decision);
        }

        return _bufferedDecisions.TryAdd(incidentId, decision);
    }

    /// <inheritdoc />
    public async Task<ApprovalDecision> WaitForApprovalAsync(
        Incident incident,
        RemediationPlan plan,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(plan);

        if (_bufferedDecisions.TryRemove(incident.IncidentId, out ApprovalDecision? buffered))
        {
            return buffered;
        }

        var waiter = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingWaits.TryAdd(incident.IncidentId, waiter))
        {
            // A concurrent wait for the same incident is a programming error;
            // refuse to guess and report a timed-out decision.
            return new ApprovalDecision(ApprovalOutcome.TimedOut);
        }

        try
        {
            return await waiter.Task
                .WaitAsync(timeout, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new ApprovalDecision(ApprovalOutcome.TimedOut);
        }
        finally
        {
            _pendingWaits.TryRemove(new KeyValuePair<string, TaskCompletionSource<ApprovalDecision>>(incident.IncidentId, waiter));
        }
    }
}
