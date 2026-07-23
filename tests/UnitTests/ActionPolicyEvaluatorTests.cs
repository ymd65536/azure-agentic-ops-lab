using AzureAgenticOps.Contracts;
using AzureAgenticOps.Safety;

namespace UnitTests;

public class ActionPolicyEvaluatorTests
{
    private static readonly ActionPolicyEvaluator Evaluator = new(ActionPolicyOptions.DemoDefaults);

    private static RemediationAction CreateAction(
        string actionType = ActionTypeCatalog.RestartDemoWorkload,
        string @namespace = "demo",
        string idempotencyKey = "inc-001:restart:1",
        int maxExecutionCount = 1) =>
        new(
            actionType,
            new ActionTarget(@namespace, "deployment", "sample-api"),
            new Dictionary<string, string>(),
            idempotencyKey,
            maxExecutionCount);

    [Fact]
    public void UnknownActionType_IsRejectedAsHighRisk()
    {
        ActionPolicyDecision decision = Evaluator.Evaluate(CreateAction(actionType: "ExecuteShellCommand"));

        Assert.False(decision.IsAllowed);
        Assert.Equal(RiskLevel.High, decision.RiskLevel);
        Assert.True(decision.RequiresApproval);
        Assert.NotEmpty(decision.RejectionReasons);
    }

    [Theory]
    [InlineData("kubectl delete namespace prod")]
    [InlineData("az group delete")]
    [InlineData("DeleteResource")]
    [InlineData("")]
    public void ArbitraryOrUndefinedActionTypes_AreAlwaysRejected(string actionType)
    {
        ActionPolicyDecision decision = Evaluator.Evaluate(CreateAction(actionType: actionType));

        Assert.False(decision.IsAllowed);
        Assert.Equal(RiskLevel.High, decision.RiskLevel);
    }

    [Fact]
    public void LowRiskAction_InDemoNamespace_IsAllowedWithoutApproval()
    {
        ActionPolicyDecision decision = Evaluator.Evaluate(CreateAction());

        Assert.True(decision.IsAllowed);
        Assert.Equal(RiskLevel.Low, decision.RiskLevel);
        Assert.False(decision.RequiresApproval);
        Assert.Empty(decision.RejectionReasons);
    }

    [Fact]
    public void LowRiskAction_WithoutAutoExecution_RequiresApproval()
    {
        var strictEvaluator = new ActionPolicyEvaluator(
            ActionPolicyOptions.DemoDefaults with { AllowAutomaticLowRiskExecution = false });

        ActionPolicyDecision decision = strictEvaluator.Evaluate(CreateAction());

        Assert.True(decision.IsAllowed);
        Assert.True(decision.RequiresApproval);
    }

    [Fact]
    public void MediumRiskAction_RequiresApproval()
    {
        ActionPolicyDecision decision = Evaluator.Evaluate(
            CreateAction(actionType: ActionTypeCatalog.RollbackDemoDeployment));

        Assert.True(decision.IsAllowed);
        Assert.Equal(RiskLevel.Medium, decision.RiskLevel);
        Assert.True(decision.RequiresApproval);
    }

    [Fact]
    public void DisallowedNamespace_IsRejected()
    {
        ActionPolicyDecision decision = Evaluator.Evaluate(CreateAction(@namespace: "kube-system"));

        Assert.False(decision.IsAllowed);
        Assert.Contains(decision.RejectionReasons, reason => reason.Contains("kube-system", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("key with spaces")]
    [InlineData("key;rm -rf /")]
    public void InvalidIdempotencyKey_IsRejected(string idempotencyKey)
    {
        ActionPolicyDecision decision = Evaluator.Evaluate(CreateAction(idempotencyKey: idempotencyKey));

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void TooLongIdempotencyKey_IsRejected()
    {
        string longKey = new('a', IdempotencyKeyValidator.MaxLength + 1);

        ActionPolicyDecision decision = Evaluator.Evaluate(CreateAction(idempotencyKey: longKey));

        Assert.False(decision.IsAllowed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100)]
    public void ExecutionCountOutsidePolicyBounds_IsRejected(int maxExecutionCount)
    {
        ActionPolicyDecision decision = Evaluator.Evaluate(CreateAction(maxExecutionCount: maxExecutionCount));

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public void Catalog_DoesNotContainArbitraryCommandActionTypes()
    {
        Assert.DoesNotContain(ActionTypeCatalog.All, definition =>
            definition.Name.Contains("Shell", StringComparison.OrdinalIgnoreCase) ||
            definition.Name.Contains("Command", StringComparison.OrdinalIgnoreCase) ||
            definition.Name.Contains("Cli", StringComparison.OrdinalIgnoreCase));
    }
}
