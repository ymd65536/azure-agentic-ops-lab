using System.Text.Json.Serialization;

namespace AzureAgenticOps.Contracts;

/// <summary>
/// The execution mode of the agent model runtime.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentExecutionMode>))]
public enum AgentExecutionMode
{
    /// <summary>Only the deterministic stub model client is used. No external communication occurs.</summary>
    [JsonStringEnumMemberName("deterministic")]
    Deterministic,

    /// <summary>The remote model's structured output is used by the workflow.</summary>
    [JsonStringEnumMemberName("remoteModel")]
    RemoteModel,

    /// <summary>
    /// The deterministic result is adopted by the workflow while the same input is
    /// also sent to the remote model and the comparison is recorded for evaluation.
    /// The remote output never reaches the workflow, approval, or execution path.
    /// </summary>
    [JsonStringEnumMemberName("shadow")]
    Shadow,
}

/// <summary>
/// The structured comparison between a deterministic result and a shadow model
/// result. Comparison covers structured fields only; free-form prose such as
/// <c>reasoningSummary</c> is never compared for equality.
/// </summary>
/// <param name="MatchesDeterministicResult">Whether all compared structured fields matched.</param>
/// <param name="MatchedFields">The names of structured fields that matched.</param>
/// <param name="MismatchedFields">The names of structured fields that did not match.</param>
/// <param name="ConfidenceDelta">The absolute confidence difference for Tier 1 results, when applicable.</param>
public sealed record EvaluationComparison(
    bool MatchesDeterministicResult,
    IReadOnlyList<string> MatchedFields,
    IReadOnlyList<string> MismatchedFields,
    double? ConfidenceDelta = null);

/// <summary>
/// A single evaluation record describing one model invocation and, in shadow
/// mode, its comparison against the deterministic result. Records are written
/// as JSON Lines under <c>results/evaluations/</c>. Incident identifiers appear
/// only in records, traces, and logs; never as metric labels.
/// </summary>
/// <param name="SchemaVersion">The contract schema version.</param>
/// <param name="IncidentId">The incident the invocation belongs to, when known.</param>
/// <param name="AgentRole">The agent role, for example "tier1" or "tier2".</param>
/// <param name="ExecutionMode">The execution mode the record was produced in.</param>
/// <param name="ScenarioName">The scenario name, when the run is scenario-driven.</param>
/// <param name="PromptName">The prompt name used for the invocation.</param>
/// <param name="PromptVersion">The prompt version used for the invocation.</param>
/// <param name="ModelId">The identifier of the model that produced the shadow or remote output, when known.</param>
/// <param name="StartedAt">When the invocation started.</param>
/// <param name="DurationMs">The total invocation duration in milliseconds.</param>
/// <param name="InputTokens">Input token count, when reported.</param>
/// <param name="OutputTokens">Output token count, when reported.</param>
/// <param name="ToolCallCount">The number of tool calls made during the invocation.</param>
/// <param name="KnowledgeRetrievalCount">The number of knowledge retrievals made during the invocation.</param>
/// <param name="SchemaValidationSucceeded">Whether structured output validation succeeded.</param>
/// <param name="RepairAttemptCount">The number of bounded repair attempts performed.</param>
/// <param name="Classification">The classification produced by the evaluated output, when applicable.</param>
/// <param name="Disposition">The disposition produced by the evaluated output, when applicable.</param>
/// <param name="RiskLevel">The risk level produced by the evaluated output, when applicable.</param>
/// <param name="ProposedActionTypes">The action types proposed by the evaluated output.</param>
/// <param name="ErrorCategory">The error category when the invocation failed, for example "timeout" or "invalid_output".</param>
/// <param name="Comparison">The structured comparison against the deterministic result, when available.</param>
public sealed record AgentEvaluationRecord(
    string SchemaVersion,
    string? IncidentId,
    string AgentRole,
    AgentExecutionMode ExecutionMode,
    string? ScenarioName,
    string PromptName,
    string PromptVersion,
    string? ModelId,
    DateTimeOffset StartedAt,
    double DurationMs,
    int? InputTokens,
    int? OutputTokens,
    int ToolCallCount,
    int KnowledgeRetrievalCount,
    bool SchemaValidationSucceeded,
    int RepairAttemptCount,
    IncidentClassification? Classification,
    AgentDisposition? Disposition,
    RiskLevel? RiskLevel,
    IReadOnlyList<string> ProposedActionTypes,
    string? ErrorCategory,
    EvaluationComparison? Comparison);
