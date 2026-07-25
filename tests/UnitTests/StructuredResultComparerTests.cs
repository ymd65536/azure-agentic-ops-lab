using AzureAgenticOps.Contracts;
using AzureAgenticOps.AgentRuntime;

namespace UnitTests;

/// <summary>Tests for structured comparison of deterministic and shadow results.</summary>
public sealed class StructuredResultComparerTests
{
    private static InvestigationResult Investigation(
        IncidentClassification classification = IncidentClassification.Known,
        AgentDisposition disposition = AgentDisposition.Resolve,
        double confidence = 0.9,
        string? actionType = "RestartDemoWorkload",
        IReadOnlyList<string>? missingEvidence = null,
        string reasoning = "reasoning") => new(
        SchemaVersions.V1,
        "inc-001",
        classification,
        "summary",
        [],
        [],
        confidence,
        disposition,
        actionType is null
            ? null
            : new RemediationAction(
                actionType,
                new ActionTarget("demo", "deployment", "svc"),
                new Dictionary<string, string>(),
                "inc-001-key"),
        missingEvidence ?? [],
        reasoning);

    private static RemediationPlan Plan(
        RiskLevel riskLevel = RiskLevel.Low,
        bool requiresApproval = false,
        string actionType = "RestartDemoWorkload",
        string checkType = "ResourceStatus",
        bool withRollback = false,
        string reasoning = "reasoning") => new(
        SchemaVersions.V1,
        "inc-001",
        "summary",
        new AgentHypothesis("cause", 0.9, []),
        riskLevel,
        requiresApproval,
        [
            new RemediationAction(
                actionType,
                new ActionTarget("demo", "deployment", "svc"),
                new Dictionary<string, string>(),
                "inc-001-key"),
        ],
        [new VerificationStep(checkType, "demo/deployment/svc", "healthy")],
        withRollback
            ?
            [
                new RemediationAction(
                    "RollbackDemoDeployment",
                    new ActionTarget("demo", "deployment", "svc"),
                    new Dictionary<string, string>(),
                    "inc-001-rollback"),
            ]
            : [],
        reasoning);

    [Fact]
    public void CompareInvestigationResults_IdenticalStructuredFields_Match()
    {
        EvaluationComparison comparison = StructuredResultComparer.CompareInvestigationResults(
            Investigation(reasoning: "deterministic prose"),
            Investigation(reasoning: "completely different model prose"));

        Assert.True(comparison.MatchesDeterministicResult);
        Assert.Empty(comparison.MismatchedFields);
        Assert.Equal(0.0, comparison.ConfidenceDelta);
    }

    [Fact]
    public void CompareInvestigationResults_Differences_AreReportedPerField()
    {
        EvaluationComparison comparison = StructuredResultComparer.CompareInvestigationResults(
            Investigation(),
            Investigation(
                classification: IncidentClassification.Ambiguous,
                disposition: AgentDisposition.Escalate,
                confidence: 0.5,
                actionType: null,
                missingEvidence: ["recent deployment diff"]));

        Assert.False(comparison.MatchesDeterministicResult);
        Assert.Contains("classification", comparison.MismatchedFields);
        Assert.Contains("recommendedDisposition", comparison.MismatchedFields);
        Assert.Contains("escalationRequired", comparison.MismatchedFields);
        Assert.Contains("proposedActionType", comparison.MismatchedFields);
        Assert.Contains("missingEvidence", comparison.MismatchedFields);
        Assert.NotNull(comparison.ConfidenceDelta);
        Assert.Equal(0.4, comparison.ConfidenceDelta.Value, precision: 6);
    }

    [Fact]
    public void CompareInvestigationResults_SameEscalationDifferentDisposition_KeepsEscalationMatched()
    {
        EvaluationComparison comparison = StructuredResultComparer.CompareInvestigationResults(
            Investigation(disposition: AgentDisposition.Resolve),
            Investigation(disposition: AgentDisposition.RequestMoreEvidence));

        Assert.Contains("recommendedDisposition", comparison.MismatchedFields);
        Assert.Contains("escalationRequired", comparison.MatchedFields);
    }

    [Fact]
    public void CompareRemediationPlans_IdenticalStructuredFields_Match()
    {
        EvaluationComparison comparison = StructuredResultComparer.CompareRemediationPlans(
            Plan(reasoning: "deterministic prose"),
            Plan(reasoning: "different model prose"));

        Assert.True(comparison.MatchesDeterministicResult);
        Assert.Empty(comparison.MismatchedFields);
    }

    [Fact]
    public void CompareRemediationPlans_Differences_AreReportedPerField()
    {
        EvaluationComparison comparison = StructuredResultComparer.CompareRemediationPlans(
            Plan(),
            Plan(
                riskLevel: RiskLevel.Medium,
                requiresApproval: true,
                actionType: "RollbackDemoDeployment",
                checkType: "HttpStatus",
                withRollback: true));

        Assert.False(comparison.MatchesDeterministicResult);
        Assert.Contains("riskLevel", comparison.MismatchedFields);
        Assert.Contains("requiresApproval", comparison.MismatchedFields);
        Assert.Contains("actionTypes", comparison.MismatchedFields);
        Assert.Contains("verificationSteps", comparison.MismatchedFields);
        Assert.Contains("rollbackPresence", comparison.MismatchedFields);
    }
}
