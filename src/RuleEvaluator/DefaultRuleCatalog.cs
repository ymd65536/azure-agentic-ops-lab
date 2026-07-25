using AzureAgenticOps.Contracts;

namespace AzureAgenticOps.RuleEvaluator;

/// <summary>
/// The default rule set for the first-milestone scenarios. Rules are defined as
/// data so they can be reviewed and tested independently of the evaluator.
/// </summary>
public static class DefaultRuleCatalog
{
    /// <summary>The pattern name for a known routing configuration error.</summary>
    public const string KnownRoutingConfigurationError = "known-routing-configuration-error";

    /// <summary>The pattern name for a known demo workload crash loop.</summary>
    public const string KnownDemoWorkloadCrashLoop = "known-demo-workload-crashloop";

    /// <summary>The pattern name for an external dependency timeout.</summary>
    public const string ExternalDependencyTimeout = "external-dependency-timeout";

    /// <summary>Gets the default rule definitions.</summary>
    public static IReadOnlyList<RuleDefinition> Rules { get; } =
    [
        new RuleDefinition(
            KnownRoutingConfigurationError,
            "HTTP 404 responses caused by a routing configuration change that references a missing route.",
            [
                new EvidenceMatchCriterion("log", "404"),
                new EvidenceMatchCriterion("config", "routeRemoved"),
            ],
            Confidence: 0.9,
            AgentDisposition.Resolve,
            EscalateToTier2: false,
            ProposedActionType: "RollbackDemoDeployment",
            MaxActionAttempts: 1),

        new RuleDefinition(
            KnownDemoWorkloadCrashLoop,
            "A disposable demo workload is crash-looping after an out-of-memory kill; restarting the workload is the approved low-risk remediation.",
            [
                new EvidenceMatchCriterion("log", "CrashLoopBackOff"),
                new EvidenceMatchCriterion("metric", "restartCount"),
            ],
            Confidence: 0.95,
            AgentDisposition.Resolve,
            EscalateToTier2: false,
            ProposedActionType: "RestartDemoWorkload",
            MaxActionAttempts: 1),

        new RuleDefinition(
            ExternalDependencyTimeout,
            "Upstream failures caused by timeouts calling an external dependency; restarting the local service does not fix the dependency.",
            [
                new EvidenceMatchCriterion("log", "upstream request timeout"),
                new EvidenceMatchCriterion("metric", "dependencyLatencyMs"),
            ],
            Confidence: 0.8,
            AgentDisposition.Escalate,
            EscalateToTier2: true,
            ProposedActionType: null,
            MaxActionAttempts: 0),
    ];
}
