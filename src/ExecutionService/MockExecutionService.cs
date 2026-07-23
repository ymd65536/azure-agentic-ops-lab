using AzureAgenticOps.Contracts;
using AzureAgenticOps.Safety;

namespace AzureAgenticOps.ExecutionService;

/// <summary>
/// A request to execute a single validated remediation action.
/// </summary>
/// <param name="IncidentId">The incident the action belongs to.</param>
/// <param name="Action">The remediation action to execute.</param>
/// <param name="ApprovalGranted">Whether human approval has been granted for this execution.</param>
/// <param name="CorrelationId">The correlation identifier for observability.</param>
public sealed record ExecutionRequest(
    string IncidentId,
    RemediationAction Action,
    bool ApprovalGranted,
    string CorrelationId);

/// <summary>
/// Executes remediation actions in mock (dry-run) mode. Every request is
/// validated against the deterministic action policy before anything happens:
/// unknown or high-risk actions are rejected, approval requirements are
/// enforced, and an idempotency ledger prevents duplicate execution beyond the
/// action's maximum execution count. No real infrastructure is modified.
/// </summary>
public sealed class MockExecutionService
{
    private readonly ActionPolicyEvaluator _policyEvaluator;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, int> _executionLedger = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Initializes a new mock execution service.</summary>
    /// <param name="policyEvaluator">The deterministic action policy evaluator.</param>
    /// <param name="timeProvider">The time provider. Defaults to <see cref="TimeProvider.System"/>.</param>
    public MockExecutionService(ActionPolicyEvaluator policyEvaluator, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(policyEvaluator);
        _policyEvaluator = policyEvaluator;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Validates and executes a remediation action in mock mode.
    /// </summary>
    /// <param name="request">The execution request.</param>
    /// <returns>The structured execution result.</returns>
    public ExecutionResult Execute(ExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        DateTimeOffset startedAt = _timeProvider.GetUtcNow();

        ActionPolicyDecision decision = _policyEvaluator.Evaluate(request.Action);
        if (!decision.IsAllowed)
        {
            return Complete(request, ExecutionOutcome.Rejected, attemptNumber: 0, startedAt,
                "Rejected by policy: " + string.Join(' ', decision.RejectionReasons));
        }

        if (decision.RequiresApproval && !request.ApprovalGranted)
        {
            return Complete(request, ExecutionOutcome.Rejected, attemptNumber: 0, startedAt,
                $"Action '{request.Action.ActionType}' requires human approval and no approval was granted.");
        }

        int attemptNumber;
        lock (_lock)
        {
            int priorExecutions = _executionLedger.GetValueOrDefault(request.Action.IdempotencyKey);
            if (priorExecutions >= request.Action.MaxExecutionCount)
            {
                return Complete(request, ExecutionOutcome.Skipped, priorExecutions, startedAt,
                    $"Idempotency key '{request.Action.IdempotencyKey}' has already been executed {priorExecutions} time(s); maximum is {request.Action.MaxExecutionCount}.");
            }

            attemptNumber = priorExecutions + 1;
            _executionLedger[request.Action.IdempotencyKey] = attemptNumber;
        }

        return Complete(request, ExecutionOutcome.Succeeded, attemptNumber, startedAt,
            $"Mock execution of '{request.Action.ActionType}' on {request.Action.Target.ResourceType}/{request.Action.Target.ResourceName} in namespace '{request.Action.Target.Namespace}' completed (dry run).");
    }

    private ExecutionResult Complete(
        ExecutionRequest request,
        ExecutionOutcome outcome,
        int attemptNumber,
        DateTimeOffset startedAt,
        string message)
    {
        return new ExecutionResult(
            SchemaVersions.V1,
            request.IncidentId,
            request.Action.ActionType,
            request.Action.IdempotencyKey,
            outcome,
            message,
            attemptNumber,
            startedAt,
            _timeProvider.GetUtcNow());
    }
}
