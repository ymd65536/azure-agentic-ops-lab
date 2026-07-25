namespace AzureAgenticOps.Observability;

/// <summary>
/// Stable names for the OpenTelemetry-compatible instrumentation emitted by the
/// system. Hosts register these names with the OpenTelemetry SDK; libraries only
/// depend on <c>System.Diagnostics</c> primitives so shared code stays free of
/// exporter and vendor SDKs.
/// </summary>
public static class ObservabilityNames
{
    /// <summary>The name of the <see cref="System.Diagnostics.ActivitySource"/> used for all spans.</summary>
    public const string ActivitySourceName = "AzureAgenticOps";

    /// <summary>The name of the <see cref="System.Diagnostics.Metrics.Meter"/> used for all metrics.</summary>
    public const string MeterName = "AzureAgenticOps";
}

/// <summary>
/// Span and metric tag names. Incident identifiers and other high-cardinality
/// values may be used as span tags but must never be used as metric labels.
/// </summary>
public static class ObservabilityTags
{
    /// <summary>The incident identifier. Spans and logs only; never metrics.</summary>
    public const string IncidentId = "incident.id";

    /// <summary>The workflow instance identifier. Spans and logs only; never metrics.</summary>
    public const string WorkflowInstanceId = "workflow.instance_id";

    /// <summary>The correlation identifier. Spans and logs only; never metrics.</summary>
    public const string CorrelationId = "correlation.id";

    /// <summary>The emitting component name. Low cardinality; allowed on metrics.</summary>
    public const string Component = "component";

    /// <summary>The attempt number for a retried operation.</summary>
    public const string AttemptNumber = "attempt.number";

    /// <summary>The outcome of an operation. Low cardinality; allowed on metrics.</summary>
    public const string Outcome = "outcome";

    /// <summary>The error category when an operation fails. Low cardinality; allowed on metrics.</summary>
    public const string ErrorCategory = "error.category";

    /// <summary>The structured action type of a remediation action. Low cardinality; allowed on metrics.</summary>
    public const string ActionType = "action.type";

    /// <summary>The agent tier ("tier1" or "tier2"). Low cardinality; allowed on metrics.</summary>
    public const string Tier = "tier";

    /// <summary>The version-controlled prompt name. Low cardinality; allowed on metrics.</summary>
    public const string PromptName = "prompt.name";

    /// <summary>The model identifier. Low cardinality; allowed on metrics.</summary>
    public const string ModelId = "model.id";

    /// <summary>The invoked tool name. Low cardinality; allowed on metrics.</summary>
    public const string ToolName = "tool.name";

    /// <summary>The terminal workflow state of a completed incident. Low cardinality; allowed on metrics.</summary>
    public const string FinalState = "workflow.final_state";
}

/// <summary>
/// Span names for the operations that must be traced. The names are stable so
/// dashboards and alerts survive refactoring.
/// </summary>
public static class SpanNames
{
    /// <summary>Incident ingestion through the API.</summary>
    public const string IncidentIngestion = "incident.ingest";

    /// <summary>A full incident workflow run.</summary>
    public const string WorkflowExecution = "workflow.execute";

    /// <summary>Evidence collection activity.</summary>
    public const string EvidenceCollection = "activity.evidence_collection";

    /// <summary>Deterministic rule evaluation activity.</summary>
    public const string RuleEvaluation = "activity.rule_evaluation";

    /// <summary>Tier 1 inference activity.</summary>
    public const string Tier1Investigation = "activity.tier1_investigation";

    /// <summary>Tier 2 inference activity.</summary>
    public const string Tier2Planning = "activity.tier2_planning";

    /// <summary>The bounded wait for a human approval event.</summary>
    public const string ApprovalWait = "workflow.approval_wait";

    /// <summary>Validated action execution activity.</summary>
    public const string Execution = "activity.execution";

    /// <summary>Verification activity.</summary>
    public const string Verification = "activity.verification";

    /// <summary>Rollback execution.</summary>
    public const string Rollback = "workflow.rollback";

    /// <summary>Scribe lifecycle event processing.</summary>
    public const string ScribeProcessing = "scribe.process";
}
