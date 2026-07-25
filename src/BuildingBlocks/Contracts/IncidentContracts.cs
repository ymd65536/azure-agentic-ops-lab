namespace AzureAgenticOps.Contracts;

/// <summary>
/// A detected operational incident submitted to the system.
/// </summary>
/// <param name="SchemaVersion">The contract schema version.</param>
/// <param name="IncidentId">The unique identifier of the incident.</param>
/// <param name="Title">A short human-readable title.</param>
/// <param name="Description">A description of the observed problem.</param>
/// <param name="Source">The system or monitor that detected the incident.</param>
/// <param name="Severity">The reported severity, for example "sev1".</param>
/// <param name="AffectedServices">The services believed to be affected.</param>
/// <param name="DetectedAt">The time the incident was detected.</param>
/// <param name="Metadata">Additional string metadata supplied by the source.</param>
public sealed record Incident(
    string SchemaVersion,
    string IncidentId,
    string Title,
    string Description,
    string Source,
    string Severity,
    IReadOnlyList<string> AffectedServices,
    DateTimeOffset DetectedAt,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// A single piece of evidence associated with an incident, such as a log excerpt,
/// a metric snapshot, or deployment metadata.
/// </summary>
/// <param name="SchemaVersion">The contract schema version.</param>
/// <param name="EvidenceId">The unique identifier of this evidence item.</param>
/// <param name="IncidentId">The incident this evidence belongs to.</param>
/// <param name="EvidenceType">The kind of evidence, for example "log", "metric", "config", or "deployment".</param>
/// <param name="Source">Where the evidence was collected from.</param>
/// <param name="Content">The evidence payload as text.</param>
/// <param name="CollectedAt">The time the evidence was collected.</param>
/// <param name="Attributes">Additional string attributes describing the evidence.</param>
public sealed record IncidentEvidence(
    string SchemaVersion,
    string EvidenceId,
    string IncidentId,
    string EvidenceType,
    string Source,
    string Content,
    DateTimeOffset CollectedAt,
    IReadOnlyDictionary<string, string>? Attributes = null);

/// <summary>
/// A hypothesis produced during investigation, with supporting evidence references.
/// </summary>
/// <param name="Description">The hypothesis statement.</param>
/// <param name="Confidence">Confidence in the hypothesis, from 0.0 to 1.0.</param>
/// <param name="EvidenceIds">Identifiers of the evidence items supporting the hypothesis.</param>
public sealed record AgentHypothesis(
    string Description,
    double Confidence,
    IReadOnlyList<string> EvidenceIds);

/// <summary>
/// The structured result of a Tier 1 investigation or rule evaluation handoff.
/// </summary>
/// <param name="SchemaVersion">The contract schema version.</param>
/// <param name="IncidentId">The incident this result belongs to.</param>
/// <param name="Classification">The classification of the incident.</param>
/// <param name="Summary">A concise summary of the investigation.</param>
/// <param name="Observations">Observable facts derived from evidence.</param>
/// <param name="Hypotheses">Candidate root-cause hypotheses.</param>
/// <param name="Confidence">Overall confidence in the result, from 0.0 to 1.0.</param>
/// <param name="RecommendedDisposition">The recommended next step.</param>
/// <param name="ProposedAction">A proposed remediation action, if a known deterministic action applies.</param>
/// <param name="MissingEvidence">Evidence that would improve the assessment.</param>
/// <param name="ReasoningSummary">A concise explanation based on observable evidence only.</param>
public sealed record InvestigationResult(
    string SchemaVersion,
    string IncidentId,
    IncidentClassification Classification,
    string Summary,
    IReadOnlyList<string> Observations,
    IReadOnlyList<AgentHypothesis> Hypotheses,
    double Confidence,
    AgentDisposition RecommendedDisposition,
    RemediationAction? ProposedAction,
    IReadOnlyList<string> MissingEvidence,
    string ReasoningSummary);

/// <summary>
/// The target of a remediation action.
/// </summary>
/// <param name="Namespace">The Kubernetes namespace or logical environment of the target.</param>
/// <param name="ResourceType">The type of the target resource, for example "deployment".</param>
/// <param name="ResourceName">The name of the target resource.</param>
public sealed record ActionTarget(
    string Namespace,
    string ResourceType,
    string ResourceName);

/// <summary>
/// A single structured remediation action. Actions reference predefined action
/// types only; arbitrary commands cannot be represented.
/// </summary>
/// <param name="ActionType">The predefined action type name.</param>
/// <param name="Target">The target of the action.</param>
/// <param name="Parameters">Named string parameters for the action.</param>
/// <param name="IdempotencyKey">A key that makes repeated execution requests idempotent.</param>
/// <param name="MaxExecutionCount">The maximum number of times this action may be executed.</param>
public sealed record RemediationAction(
    string ActionType,
    ActionTarget Target,
    IReadOnlyDictionary<string, string> Parameters,
    string IdempotencyKey,
    int MaxExecutionCount = 1);

/// <summary>
/// A single verification step used to confirm remediation success.
/// </summary>
/// <param name="CheckType">The kind of check, for example "HttpStatus" or "PodReady".</param>
/// <param name="Target">The target of the check.</param>
/// <param name="ExpectedValue">The expected result value as text.</param>
/// <param name="TimeoutSeconds">The maximum time allowed for the check.</param>
public sealed record VerificationStep(
    string CheckType,
    string Target,
    string ExpectedValue,
    int TimeoutSeconds = 30);

/// <summary>
/// A structured remediation plan produced by Tier 2 investigation.
/// </summary>
/// <param name="SchemaVersion">The contract schema version.</param>
/// <param name="IncidentId">The incident this plan addresses.</param>
/// <param name="Summary">A concise summary of the plan.</param>
/// <param name="RootCauseHypothesis">The most likely root cause.</param>
/// <param name="RiskLevel">The overall risk level of the plan.</param>
/// <param name="RequiresApproval">Whether human approval is required before execution.</param>
/// <param name="Actions">The ordered remediation actions.</param>
/// <param name="Verification">The verification steps to run after execution.</param>
/// <param name="Rollback">The rollback actions, when rollback is possible.</param>
/// <param name="ReasoningSummary">A concise explanation based on observable evidence only.</param>
public sealed record RemediationPlan(
    string SchemaVersion,
    string IncidentId,
    string Summary,
    AgentHypothesis RootCauseHypothesis,
    RiskLevel RiskLevel,
    bool RequiresApproval,
    IReadOnlyList<RemediationAction> Actions,
    IReadOnlyList<VerificationStep> Verification,
    IReadOnlyList<RemediationAction> Rollback,
    string ReasoningSummary);

/// <summary>
/// The result of executing a single remediation action.
/// </summary>
/// <param name="SchemaVersion">The contract schema version.</param>
/// <param name="IncidentId">The incident the action belongs to.</param>
/// <param name="ActionType">The executed action type.</param>
/// <param name="IdempotencyKey">The idempotency key of the execution request.</param>
/// <param name="Outcome">The execution outcome.</param>
/// <param name="Message">A human-readable description of the outcome.</param>
/// <param name="AttemptNumber">The attempt number of this execution, starting at 1.</param>
/// <param name="StartedAt">When execution started.</param>
/// <param name="CompletedAt">When execution completed.</param>
public sealed record ExecutionResult(
    string SchemaVersion,
    string IncidentId,
    string ActionType,
    string IdempotencyKey,
    ExecutionOutcome Outcome,
    string Message,
    int AttemptNumber,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

/// <summary>
/// The result of a single verification check.
/// </summary>
/// <param name="CheckType">The kind of check performed.</param>
/// <param name="Target">The target of the check.</param>
/// <param name="ExpectedValue">The expected result value as text.</param>
/// <param name="ActualValue">The observed result value as text.</param>
/// <param name="Passed">Whether the check passed.</param>
public sealed record VerificationCheckResult(
    string CheckType,
    string Target,
    string ExpectedValue,
    string ActualValue,
    bool Passed);

/// <summary>
/// The overall result of a verification pass after remediation.
/// </summary>
/// <param name="SchemaVersion">The contract schema version.</param>
/// <param name="IncidentId">The incident that was verified.</param>
/// <param name="Outcome">The overall verification outcome.</param>
/// <param name="CheckResults">The individual check results.</param>
/// <param name="CompletedAt">When verification completed.</param>
public sealed record VerificationResult(
    string SchemaVersion,
    string IncidentId,
    VerificationOutcome Outcome,
    IReadOnlyList<VerificationCheckResult> CheckResults,
    DateTimeOffset CompletedAt);

/// <summary>
/// An auditable lifecycle event emitted whenever an incident changes state or a
/// component records an outcome.
/// </summary>
/// <param name="SchemaVersion">The contract schema version.</param>
/// <param name="EventId">The unique identifier of the event.</param>
/// <param name="IncidentId">The incident the event belongs to.</param>
/// <param name="CorrelationId">The correlation identifier linking related operations.</param>
/// <param name="EventType">The event type, for example "IncidentReceived" or "ExecutionCompleted".</param>
/// <param name="Component">The component that emitted the event.</param>
/// <param name="OccurredAt">When the event occurred.</param>
/// <param name="AttemptNumber">The attempt number for retried operations, starting at 1.</param>
/// <param name="Outcome">The outcome of the operation, when applicable.</param>
/// <param name="WorkflowInstanceId">The workflow instance identifier, when the event is part of a workflow.</param>
/// <param name="Details">Additional string details describing the event.</param>
public sealed record IncidentLifecycleEvent(
    string SchemaVersion,
    string EventId,
    string IncidentId,
    string CorrelationId,
    string EventType,
    string Component,
    DateTimeOffset OccurredAt,
    int AttemptNumber = 1,
    string? Outcome = null,
    string? WorkflowInstanceId = null,
    IReadOnlyDictionary<string, string>? Details = null);
