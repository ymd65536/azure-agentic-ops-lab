using AzureAgenticOps.Contracts;

namespace AzureAgenticOps.Safety;

/// <summary>
/// The definition of a single allowed action type, including its fixed risk
/// classification. Agents cannot downgrade the risk level defined here.
/// </summary>
/// <param name="Name">The action type name.</param>
/// <param name="RiskLevel">The fixed risk classification of the action.</param>
/// <param name="Description">A description of what the action does.</param>
public sealed record ActionTypeDefinition(
    string Name,
    RiskLevel RiskLevel,
    string Description);

/// <summary>
/// The allow-list of predefined action types. Only actions defined here may be
/// requested; unknown action types are rejected and treated as high risk.
/// Arbitrary shell, Kubernetes, or Azure CLI commands cannot be represented.
/// </summary>
public static class ActionTypeCatalog
{
    /// <summary>Collects diagnostics from a target workload.</summary>
    public const string CollectDiagnostics = "CollectDiagnostics";

    /// <summary>Queries logs for a target workload.</summary>
    public const string QueryLogs = "QueryLogs";

    /// <summary>Queries the status of a target resource.</summary>
    public const string QueryResourceStatus = "QueryResourceStatus";

    /// <summary>Restarts a disposable demo workload.</summary>
    public const string RestartDemoWorkload = "RestartDemoWorkload";

    /// <summary>Scales a demo workload within a predefined range.</summary>
    public const string ScaleDemoWorkload = "ScaleDemoWorkload";

    /// <summary>Rolls back a demo deployment to its previous revision.</summary>
    public const string RollbackDemoDeployment = "RollbackDemoDeployment";

    private static readonly IReadOnlyDictionary<string, ActionTypeDefinition> Definitions =
        new Dictionary<string, ActionTypeDefinition>(StringComparer.Ordinal)
        {
            [CollectDiagnostics] = new(CollectDiagnostics, RiskLevel.Low, "Collect diagnostics from a target workload."),
            [QueryLogs] = new(QueryLogs, RiskLevel.Low, "Query logs for a target workload."),
            [QueryResourceStatus] = new(QueryResourceStatus, RiskLevel.Low, "Query the status of a target resource."),
            [RestartDemoWorkload] = new(RestartDemoWorkload, RiskLevel.Low, "Restart a disposable demo workload."),
            [ScaleDemoWorkload] = new(ScaleDemoWorkload, RiskLevel.Low, "Scale a demo workload within a predefined range."),
            [RollbackDemoDeployment] = new(RollbackDemoDeployment, RiskLevel.Medium, "Roll back a demo deployment to its previous revision."),
        };

    /// <summary>Gets all allowed action type definitions.</summary>
    public static IReadOnlyCollection<ActionTypeDefinition> All => Definitions.Values.ToArray();

    /// <summary>Determines whether the action type is on the allow-list.</summary>
    /// <param name="actionType">The action type name to check.</param>
    /// <returns><see langword="true"/> when the action type is allowed.</returns>
    public static bool IsKnown(string actionType) =>
        actionType is not null && Definitions.ContainsKey(actionType);

    /// <summary>Attempts to resolve the definition for an action type.</summary>
    /// <param name="actionType">The action type name.</param>
    /// <param name="definition">The resolved definition, when found.</param>
    /// <returns><see langword="true"/> when the action type is allowed.</returns>
    public static bool TryGet(string actionType, out ActionTypeDefinition? definition)
    {
        if (actionType is not null && Definitions.TryGetValue(actionType, out ActionTypeDefinition? found))
        {
            definition = found;
            return true;
        }

        definition = null;
        return false;
    }
}
