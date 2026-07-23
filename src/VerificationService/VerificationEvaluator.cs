using AzureAgenticOps.Contracts;

namespace AzureAgenticOps.VerificationService;

/// <summary>
/// Runs a single verification check against a target. Implementations must be
/// deterministic about success and failure; a mock runner is used until real
/// health probes are wired in.
/// </summary>
public interface IVerificationCheckRunner
{
    /// <summary>Runs a single verification step.</summary>
    /// <param name="step">The verification step to run.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The check result.</returns>
    Task<VerificationCheckResult> RunAsync(VerificationStep step, CancellationToken cancellationToken);
}

/// <summary>
/// A deterministic mock check runner for the local demo environment. Results are
/// configured per check target; unconfigured targets produce a failed check with
/// an explicit "no result configured" actual value so verification never guesses.
/// </summary>
public sealed class MockVerificationCheckRunner : IVerificationCheckRunner
{
    private readonly Dictionary<string, string> _actualValuesByTarget = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Configures the actual value observed for a check target.</summary>
    /// <param name="target">The check target.</param>
    /// <param name="actualValue">The value the mock will report for the target.</param>
    public void SetActualValue(string target, string actualValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(actualValue);
        lock (_lock)
        {
            _actualValuesByTarget[target] = actualValue;
        }
    }

    /// <inheritdoc />
    public Task<VerificationCheckResult> RunAsync(VerificationStep step, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        cancellationToken.ThrowIfCancellationRequested();

        string? actualValue;
        lock (_lock)
        {
            _actualValuesByTarget.TryGetValue(step.Target, out actualValue);
        }

        actualValue ??= "no result configured";
        bool passed = string.Equals(actualValue, step.ExpectedValue, StringComparison.Ordinal);

        return Task.FromResult(new VerificationCheckResult(
            step.CheckType,
            step.Target,
            step.ExpectedValue,
            actualValue,
            passed));
    }
}

/// <summary>
/// Runs all verification steps of a remediation plan and aggregates the outcome
/// deterministically: every check must pass for the verification to pass, any
/// failed check fails the verification, and an empty step list is inconclusive
/// because success cannot be demonstrated.
/// </summary>
public sealed class VerificationEvaluator
{
    private readonly IVerificationCheckRunner _checkRunner;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new evaluator.</summary>
    /// <param name="checkRunner">The runner used for individual checks.</param>
    /// <param name="timeProvider">The time provider. Defaults to <see cref="TimeProvider.System"/>.</param>
    public VerificationEvaluator(IVerificationCheckRunner checkRunner, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(checkRunner);
        _checkRunner = checkRunner;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Runs the supplied verification steps and produces the aggregated result.
    /// </summary>
    /// <param name="incidentId">The incident being verified.</param>
    /// <param name="steps">The verification steps to run.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The aggregated verification result.</returns>
    public async Task<VerificationResult> VerifyAsync(
        string incidentId,
        IReadOnlyList<VerificationStep> steps,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incidentId);
        ArgumentNullException.ThrowIfNull(steps);

        if (steps.Count == 0)
        {
            return new VerificationResult(
                SchemaVersions.V1,
                incidentId,
                VerificationOutcome.Inconclusive,
                [],
                _timeProvider.GetUtcNow());
        }

        var checkResults = new List<VerificationCheckResult>(steps.Count);
        foreach (VerificationStep step in steps)
        {
            VerificationCheckResult result =
                await _checkRunner.RunAsync(step, cancellationToken).ConfigureAwait(false);
            checkResults.Add(result);
        }

        VerificationOutcome outcome = checkResults.All(result => result.Passed)
            ? VerificationOutcome.Passed
            : VerificationOutcome.Failed;

        return new VerificationResult(
            SchemaVersions.V1,
            incidentId,
            outcome,
            checkResults,
            _timeProvider.GetUtcNow());
    }
}
