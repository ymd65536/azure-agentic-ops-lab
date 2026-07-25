using AzureAgenticOps.IncidentWorkflow;

namespace AzureAgenticOps.OpsConsole;

/// <summary>
/// Options for the operations console.
/// </summary>
public sealed class OpsConsoleOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "OpsConsole";

    /// <summary>Gets or sets the base address of the IncidentApi.</summary>
    public string IncidentApiBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>Gets or sets the directory holding the scenario fixtures.</summary>
    public string ScenariosRoot { get; set; } = "scenarios";

    /// <summary>Gets or sets the polling interval, in seconds, used to refresh views.</summary>
    public double RefreshIntervalSeconds { get; set; } = 2;
}

/// <summary>
/// The status of one incident workflow run as reported by the IncidentApi.
/// </summary>
/// <param name="IncidentId">The incident being processed.</param>
/// <param name="WorkflowInstanceId">The workflow instance identifier.</param>
/// <param name="CorrelationId">The correlation identifier for related operations.</param>
/// <param name="CurrentState">The most recently observed workflow state.</param>
/// <param name="IsCompleted">Whether the workflow reached a terminal state.</param>
/// <param name="Result">The final workflow result, when completed.</param>
/// <param name="Title">The incident title.</param>
/// <param name="Severity">The reported incident severity.</param>
/// <param name="StartedAt">When the run started.</param>
public sealed record IncidentRunView(
    string IncidentId,
    string WorkflowInstanceId,
    string CorrelationId,
    IncidentWorkflowState CurrentState,
    bool IsCompleted,
    IncidentWorkflowResult? Result,
    string? Title = null,
    string? Severity = null,
    DateTimeOffset? StartedAt = null);
