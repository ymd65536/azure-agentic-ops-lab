using System.Text.Json.Serialization;

namespace AzureAgenticOps.IncidentWorkflow;

/// <summary>
/// The explicit states of the incident workflow. The workflow engine, not the
/// agents, owns every transition between these states.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<IncidentWorkflowState>))]
public enum IncidentWorkflowState
{
    /// <summary>The incident has been received and the workflow has started.</summary>
    [JsonStringEnumMemberName("received")]
    Received,

    /// <summary>Evidence is being collected and the incident is being classified.</summary>
    [JsonStringEnumMemberName("classifying")]
    Classifying,

    /// <summary>Deterministic rule evaluation is running.</summary>
    [JsonStringEnumMemberName("ruleEvaluation")]
    RuleEvaluation,

    /// <summary>The Tier 1 agent is investigating.</summary>
    [JsonStringEnumMemberName("tier1Investigation")]
    Tier1Investigation,

    /// <summary>Additional evidence is being collected at Tier 1's request.</summary>
    [JsonStringEnumMemberName("awaitingEvidence")]
    AwaitingEvidence,

    /// <summary>The Tier 2 agent is investigating and planning remediation.</summary>
    [JsonStringEnumMemberName("tier2Investigation")]
    Tier2Investigation,

    /// <summary>The workflow is waiting for an external human approval event.</summary>
    [JsonStringEnumMemberName("awaitingApproval")]
    AwaitingApproval,

    /// <summary>Validated remediation actions are being executed.</summary>
    [JsonStringEnumMemberName("executing")]
    Executing,

    /// <summary>Remediation success is being verified.</summary>
    [JsonStringEnumMemberName("verifying")]
    Verifying,

    /// <summary>Rollback actions are being executed after a failure.</summary>
    [JsonStringEnumMemberName("rollingBack")]
    RollingBack,

    /// <summary>Terminal: the incident was remediated and verified.</summary>
    [JsonStringEnumMemberName("resolved")]
    Resolved,

    /// <summary>Terminal: a human rejected the remediation plan.</summary>
    [JsonStringEnumMemberName("rejected")]
    Rejected,

    /// <summary>Terminal: the workflow stopped safely after an unrecoverable failure.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>Terminal: the workflow was terminated, for example after an approval timeout.</summary>
    [JsonStringEnumMemberName("terminated")]
    Terminated,
}
