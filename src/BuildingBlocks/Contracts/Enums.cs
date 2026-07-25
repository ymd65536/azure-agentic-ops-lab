using System.Text.Json.Serialization;

namespace AzureAgenticOps.Contracts;

/// <summary>
/// Classification of an incident after rule evaluation or Tier 1 investigation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<IncidentClassification>))]
public enum IncidentClassification
{
    /// <summary>The incident matches a known operational pattern.</summary>
    [JsonStringEnumMemberName("known")]
    Known,

    /// <summary>The incident does not match any known operational pattern.</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    /// <summary>The incident partially matches one or more patterns and requires deeper analysis.</summary>
    [JsonStringEnumMemberName("ambiguous")]
    Ambiguous,
}

/// <summary>
/// The disposition recommended by an agent or the rule evaluator.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentDisposition>))]
public enum AgentDisposition
{
    /// <summary>The incident can be resolved with an approved deterministic action.</summary>
    [JsonStringEnumMemberName("resolve")]
    Resolve,

    /// <summary>The incident must be escalated to the next tier.</summary>
    [JsonStringEnumMemberName("escalate")]
    Escalate,

    /// <summary>More evidence is required before a decision can be made.</summary>
    [JsonStringEnumMemberName("request_more_evidence")]
    RequestMoreEvidence,
}

/// <summary>
/// The risk level assigned to a remediation action or plan.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RiskLevel>))]
public enum RiskLevel
{
    /// <summary>Low-risk action, such as collecting diagnostics or restarting a demo workload.</summary>
    [JsonStringEnumMemberName("low")]
    Low,

    /// <summary>Medium-risk action requiring approval by default.</summary>
    [JsonStringEnumMemberName("medium")]
    Medium,

    /// <summary>High-risk action rejected in the initial implementation.</summary>
    [JsonStringEnumMemberName("high")]
    High,
}

/// <summary>
/// The outcome of executing a remediation action.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExecutionOutcome>))]
public enum ExecutionOutcome
{
    /// <summary>The action completed successfully.</summary>
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,

    /// <summary>The action failed during execution.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>The action was rejected by policy before execution.</summary>
    [JsonStringEnumMemberName("rejected")]
    Rejected,

    /// <summary>The action was skipped, for example because of idempotency.</summary>
    [JsonStringEnumMemberName("skipped")]
    Skipped,
}

/// <summary>
/// The outcome of a verification pass.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<VerificationOutcome>))]
public enum VerificationOutcome
{
    /// <summary>All verification steps passed.</summary>
    [JsonStringEnumMemberName("passed")]
    Passed,

    /// <summary>One or more verification steps failed.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>Verification could not produce a definitive result.</summary>
    [JsonStringEnumMemberName("inconclusive")]
    Inconclusive,
}
