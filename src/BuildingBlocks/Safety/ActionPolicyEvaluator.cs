using AzureAgenticOps.Contracts;

namespace AzureAgenticOps.Safety;

/// <summary>
/// Options controlling action policy evaluation.
/// </summary>
/// <param name="AllowAutomaticLowRiskExecution">
/// Whether low-risk actions may execute without approval. This must only be
/// enabled in explicitly configured demo environments.
/// </param>
/// <param name="AllowedNamespaces">
/// The namespaces in which actions may be executed. An empty list rejects all targets.
/// </param>
/// <param name="MaxExecutionCount">The maximum permitted value for an action's execution count.</param>
public sealed record ActionPolicyOptions(
    bool AllowAutomaticLowRiskExecution,
    IReadOnlyList<string> AllowedNamespaces,
    int MaxExecutionCount = 3)
{
    /// <summary>Gets a conservative default policy for the local demo environment.</summary>
    public static ActionPolicyOptions DemoDefaults { get; } =
        new(AllowAutomaticLowRiskExecution: true, AllowedNamespaces: ["demo"]);
}

/// <summary>
/// The decision produced by <see cref="ActionPolicyEvaluator"/> for a single action.
/// Policy code has final authority over model output.
/// </summary>
/// <param name="IsAllowed">Whether the action may proceed.</param>
/// <param name="RiskLevel">The authoritative risk level assigned by policy.</param>
/// <param name="RequiresApproval">Whether human approval is required before execution.</param>
/// <param name="RejectionReasons">The reasons the action was rejected, when rejected.</param>
public sealed record ActionPolicyDecision(
    bool IsAllowed,
    RiskLevel RiskLevel,
    bool RequiresApproval,
    IReadOnlyList<string> RejectionReasons);

/// <summary>
/// Deterministic policy evaluation for remediation actions. Evaluation enforces
/// the allow-list, rejects unknown and high-risk actions, requires approval for
/// medium-risk actions, and validates targets and idempotency keys. Agents
/// cannot downgrade the risk classification assigned here.
/// </summary>
public sealed class ActionPolicyEvaluator
{
    private readonly ActionPolicyOptions _options;

    /// <summary>Initializes a new evaluator with the supplied policy options.</summary>
    /// <param name="options">The policy options.</param>
    public ActionPolicyEvaluator(ActionPolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Evaluates a remediation action against policy.
    /// </summary>
    /// <param name="action">The requested action.</param>
    /// <returns>The authoritative policy decision.</returns>
    public ActionPolicyDecision Evaluate(RemediationAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var rejectionReasons = new List<string>();

        // Unknown action types are treated as high risk and rejected.
        if (!ActionTypeCatalog.TryGet(action.ActionType, out ActionTypeDefinition? definition))
        {
            rejectionReasons.Add($"Action type '{action.ActionType}' is not on the allow-list and is treated as high risk.");
            return new ActionPolicyDecision(
                IsAllowed: false,
                RiskLevel.High,
                RequiresApproval: true,
                rejectionReasons);
        }

        RiskLevel riskLevel = definition!.RiskLevel;

        if (riskLevel == RiskLevel.High)
        {
            rejectionReasons.Add($"Action type '{action.ActionType}' is classified as high risk. High-risk actions are rejected in this milestone.");
        }

        if (action.Target is null || string.IsNullOrWhiteSpace(action.Target.Namespace))
        {
            rejectionReasons.Add("Action target namespace must be specified.");
        }
        else if (!_options.AllowedNamespaces.Contains(action.Target.Namespace, StringComparer.Ordinal))
        {
            rejectionReasons.Add($"Namespace '{action.Target.Namespace}' is not an allowed target namespace.");
        }

        if (!IdempotencyKeyValidator.IsValid(action.IdempotencyKey, out string? keyFailure))
        {
            rejectionReasons.Add(keyFailure!);
        }

        if (action.MaxExecutionCount < 1 || action.MaxExecutionCount > _options.MaxExecutionCount)
        {
            rejectionReasons.Add($"Max execution count must be between 1 and {_options.MaxExecutionCount}.");
        }

        bool requiresApproval = riskLevel switch
        {
            RiskLevel.Low => !_options.AllowAutomaticLowRiskExecution,
            RiskLevel.Medium => true,
            _ => true,
        };

        return new ActionPolicyDecision(
            IsAllowed: rejectionReasons.Count == 0,
            riskLevel,
            requiresApproval,
            rejectionReasons);
    }
}
