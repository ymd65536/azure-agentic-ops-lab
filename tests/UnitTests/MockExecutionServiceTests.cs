using AzureAgenticOps.Contracts;
using AzureAgenticOps.ExecutionService;
using AzureAgenticOps.Safety;

namespace UnitTests;

/// <summary>Tests for the mock execution service's policy, approval, and idempotency behavior.</summary>
public sealed class MockExecutionServiceTests
{
    private static MockExecutionService CreateService() =>
        new(new ActionPolicyEvaluator(ActionPolicyOptions.DemoDefaults));

    private static RemediationAction Action(
        string actionType = ActionTypeCatalog.RestartDemoWorkload,
        string idempotencyKey = "inc-001-restart-1",
        string targetNamespace = "demo",
        int maxExecutionCount = 1)
    {
        return new RemediationAction(
            actionType,
            new ActionTarget(targetNamespace, "deployment", "sample-api"),
            new Dictionary<string, string>(),
            idempotencyKey,
            maxExecutionCount);
    }

    [Fact]
    public void Execute_AllowedLowRiskAction_SucceedsInMockMode()
    {
        ExecutionResult result = CreateService().Execute(
            new ExecutionRequest("inc-001", Action(), ApprovalGranted: false, "corr-1"));

        Assert.Equal(ExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, result.AttemptNumber);
        Assert.Contains("dry run", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_UnknownActionType_IsRejected()
    {
        ExecutionResult result = CreateService().Execute(
            new ExecutionRequest("inc-001", Action(actionType: "DeleteNamespace"), ApprovalGranted: true, "corr-1"));

        Assert.Equal(ExecutionOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public void Execute_MediumRiskActionWithoutApproval_IsRejected()
    {
        ExecutionResult result = CreateService().Execute(new ExecutionRequest(
            "inc-001",
            Action(actionType: ActionTypeCatalog.RollbackDemoDeployment, idempotencyKey: "inc-001-rollback-1"),
            ApprovalGranted: false,
            "corr-1"));

        Assert.Equal(ExecutionOutcome.Rejected, result.Outcome);
        Assert.Contains("approval", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_MediumRiskActionWithApproval_Succeeds()
    {
        ExecutionResult result = CreateService().Execute(new ExecutionRequest(
            "inc-001",
            Action(actionType: ActionTypeCatalog.RollbackDemoDeployment, idempotencyKey: "inc-001-rollback-1"),
            ApprovalGranted: true,
            "corr-1"));

        Assert.Equal(ExecutionOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public void Execute_DisallowedNamespace_IsRejected()
    {
        ExecutionResult result = CreateService().Execute(new ExecutionRequest(
            "inc-001", Action(targetNamespace: "kube-system"), ApprovalGranted: true, "corr-1"));

        Assert.Equal(ExecutionOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public void Execute_DuplicateIdempotencyKey_IsSkippedAfterMaxExecutions()
    {
        MockExecutionService service = CreateService();
        var request = new ExecutionRequest("inc-001", Action(), ApprovalGranted: false, "corr-1");

        ExecutionResult first = service.Execute(request);
        ExecutionResult second = service.Execute(request);

        Assert.Equal(ExecutionOutcome.Succeeded, first.Outcome);
        Assert.Equal(ExecutionOutcome.Skipped, second.Outcome);
        Assert.Equal(1, second.AttemptNumber);
    }

    [Fact]
    public void Execute_RespectsMaxExecutionCountAboveOne()
    {
        MockExecutionService service = CreateService();
        var request = new ExecutionRequest(
            "inc-001", Action(maxExecutionCount: 2), ApprovalGranted: false, "corr-1");

        Assert.Equal(ExecutionOutcome.Succeeded, service.Execute(request).Outcome);
        Assert.Equal(ExecutionOutcome.Succeeded, service.Execute(request).Outcome);
        Assert.Equal(ExecutionOutcome.Skipped, service.Execute(request).Outcome);
    }
}
