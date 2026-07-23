using System.Text.Json.Serialization;
using AzureAgenticOps.Contracts;

namespace AzureAgenticOps.IncidentWorkflow;

/// <summary>
/// The outcome of a human approval wait.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ApprovalOutcome>))]
public enum ApprovalOutcome
{
    /// <summary>The plan was approved.</summary>
    [JsonStringEnumMemberName("approved")]
    Approved,

    /// <summary>The plan was rejected.</summary>
    [JsonStringEnumMemberName("rejected")]
    Rejected,

    /// <summary>No decision arrived before the configured timeout.</summary>
    [JsonStringEnumMemberName("timedOut")]
    TimedOut,
}

/// <summary>
/// A human approval decision delivered to the workflow as an external event.
/// </summary>
/// <param name="Outcome">The decision outcome.</param>
/// <param name="Approver">The identity of the approver, when a decision was made.</param>
/// <param name="Reason">The stated reason for the decision, when supplied.</param>
public sealed record ApprovalDecision(
    ApprovalOutcome Outcome,
    string? Approver = null,
    string? Reason = null);

/// <summary>
/// Waits for an external human approval event. In the Dapr-hosted deployment the
/// implementation waits on a workflow external event; no HTTP request is held
/// open. Implementations must return <see cref="ApprovalOutcome.TimedOut"/> when
/// no decision arrives within the timeout instead of throwing.
/// </summary>
public interface IApprovalGate
{
    /// <summary>Waits for an approval decision for the supplied plan.</summary>
    /// <param name="incident">The incident awaiting approval.</param>
    /// <param name="plan">The remediation plan requiring approval.</param>
    /// <param name="timeout">The maximum time to wait for a decision.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The approval decision, or a timed-out decision.</returns>
    Task<ApprovalDecision> WaitForApprovalAsync(
        Incident incident,
        RemediationPlan plan,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
