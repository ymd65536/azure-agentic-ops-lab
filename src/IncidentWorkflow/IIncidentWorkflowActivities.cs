using AzureAgenticOps.Contracts;
using AzureAgenticOps.RuleEvaluator;

namespace AzureAgenticOps.IncidentWorkflow;

/// <summary>
/// The activities invoked by the incident workflow orchestrator. Each member maps
/// to a service boundary: in the Dapr-hosted deployment, implementations call the
/// corresponding service through Dapr service invocation, while tests supply
/// deterministic fakes. The orchestrator never talks to a model or to
/// infrastructure directly.
/// </summary>
public interface IIncidentWorkflowActivities
{
    /// <summary>Collects evidence for the incident.</summary>
    /// <param name="incident">The incident under investigation.</param>
    /// <param name="attemptNumber">The collection attempt number, starting at 1.</param>
    /// <param name="correlationId">The correlation identifier for observability.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The evidence collected so far, including prior evidence.</returns>
    Task<IReadOnlyList<IncidentEvidence>> CollectEvidenceAsync(
        Incident incident,
        int attemptNumber,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>Runs deterministic rule evaluation.</summary>
    /// <param name="incident">The incident under investigation.</param>
    /// <param name="evidence">The evidence collected for the incident.</param>
    /// <param name="correlationId">The correlation identifier for observability.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The deterministic rule evaluation result.</returns>
    Task<RuleEvaluationResult> EvaluateRulesAsync(
        Incident incident,
        IReadOnlyList<IncidentEvidence> evidence,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>Runs the Tier 1 investigation.</summary>
    /// <param name="incident">The incident under investigation.</param>
    /// <param name="evidence">The evidence collected for the incident.</param>
    /// <param name="correlationId">The correlation identifier for observability.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The validated Tier 1 investigation result.</returns>
    Task<InvestigationResult> RunTier1InvestigationAsync(
        Incident incident,
        IReadOnlyList<IncidentEvidence> evidence,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>Runs Tier 2 remediation planning.</summary>
    /// <param name="incident">The incident under investigation.</param>
    /// <param name="tier1Handoff">The complete structured Tier 1 handoff.</param>
    /// <param name="evidence">The evidence collected for the incident.</param>
    /// <param name="correlationId">The correlation identifier for observability.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The validated remediation plan.</returns>
    Task<RemediationPlan> RunTier2PlanningAsync(
        Incident incident,
        InvestigationResult tier1Handoff,
        IReadOnlyList<IncidentEvidence> evidence,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>Executes a single validated remediation action.</summary>
    /// <param name="incident">The incident the action belongs to.</param>
    /// <param name="action">The remediation action to execute.</param>
    /// <param name="approvalGranted">Whether human approval has been granted.</param>
    /// <param name="correlationId">The correlation identifier for observability.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The structured execution result.</returns>
    Task<ExecutionResult> ExecuteActionAsync(
        Incident incident,
        RemediationAction action,
        bool approvalGranted,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>Verifies a remediation executed on the Tier 1 fast path.</summary>
    /// <param name="incident">The incident that was remediated.</param>
    /// <param name="executedAction">The action that was executed.</param>
    /// <param name="correlationId">The correlation identifier for observability.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The verification result.</returns>
    Task<VerificationResult> VerifyTier1RemediationAsync(
        Incident incident,
        RemediationAction executedAction,
        string correlationId,
        CancellationToken cancellationToken);

    /// <summary>Runs the verification steps of a Tier 2 remediation plan.</summary>
    /// <param name="incident">The incident that was remediated.</param>
    /// <param name="plan">The executed remediation plan.</param>
    /// <param name="correlationId">The correlation identifier for observability.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The verification result.</returns>
    Task<VerificationResult> VerifyPlanAsync(
        Incident incident,
        RemediationPlan plan,
        string correlationId,
        CancellationToken cancellationToken);
}
