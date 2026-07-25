using AzureAgenticOps.Contracts;
using AzureAgenticOps.IncidentWorkflow;

namespace AzureAgenticOps.IncidentApi;

/// <summary>
/// The request body for submitting an incident. Evidence is optional mock data
/// supplied with the incident during the local milestone.
/// </summary>
/// <param name="Incident">The incident contract.</param>
/// <param name="Evidence">The evidence items collected for the incident.</param>
public sealed record IncidentSubmission(
    Incident Incident,
    IReadOnlyList<IncidentEvidence>? Evidence);

/// <summary>
/// The request body delivering a human approval decision as an external event.
/// </summary>
/// <param name="Outcome">The decision: approved or rejected.</param>
/// <param name="Approver">The identity of the approver.</param>
/// <param name="Reason">The stated reason for the decision.</param>
public sealed record ApprovalSubmission(
    ApprovalOutcome Outcome,
    string? Approver = null,
    string? Reason = null);

/// <summary>
/// The request body configuring the mock verification runner for the local demo.
/// </summary>
/// <param name="Target">The verification check target.</param>
/// <param name="ActualValue">The value the mock reports for the target.</param>
public sealed record VerificationOverrideSubmission(
    string Target,
    string ActualValue);
