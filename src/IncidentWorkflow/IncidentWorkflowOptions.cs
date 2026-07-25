namespace AzureAgenticOps.IncidentWorkflow;

/// <summary>
/// Options bounding the incident workflow. Every loop in the orchestrator is
/// limited by one of these counts so that the workflow always terminates safely.
/// </summary>
/// <param name="MaxEvidenceCollectionAttempts">The maximum number of evidence collection attempts.</param>
/// <param name="MaxTier1Attempts">The maximum number of Tier 1 investigation attempts, including re-investigation after new evidence.</param>
/// <param name="MaxTier2Attempts">The maximum number of Tier 2 planning attempts, including re-planning after failed verification.</param>
/// <param name="MaxExecutionAttemptsPerAction">The maximum number of execution attempts per remediation action.</param>
/// <param name="MaxVerificationAttempts">The maximum number of verification attempts per remediation.</param>
/// <param name="MaxRollbackAttemptsPerAction">The maximum number of execution attempts per rollback action.</param>
/// <param name="ApprovalTimeout">The maximum time to wait for a human approval decision.</param>
public sealed record IncidentWorkflowOptions(
    int MaxEvidenceCollectionAttempts = 2,
    int MaxTier1Attempts = 2,
    int MaxTier2Attempts = 2,
    int MaxExecutionAttemptsPerAction = 2,
    int MaxVerificationAttempts = 2,
    int MaxRollbackAttemptsPerAction = 1,
    TimeSpan ApprovalTimeout = default)
{
    /// <summary>Gets the default workflow options with a 15 minute approval timeout.</summary>
    public static IncidentWorkflowOptions Default { get; } = new();

    /// <summary>Gets the effective approval timeout, defaulting to 15 minutes.</summary>
    public TimeSpan EffectiveApprovalTimeout =>
        ApprovalTimeout == default ? TimeSpan.FromMinutes(15) : ApprovalTimeout;
}
