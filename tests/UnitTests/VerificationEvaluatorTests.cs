using AzureAgenticOps.Contracts;
using AzureAgenticOps.VerificationService;

namespace UnitTests;

/// <summary>Tests for deterministic verification aggregation.</summary>
public sealed class VerificationEvaluatorTests
{
    private static VerificationStep Step(string target = "http://sample-api/api/orders", string expected = "200") =>
        new("HttpStatus", target, expected);

    [Fact]
    public async Task Verify_NoSteps_IsInconclusive()
    {
        var evaluator = new VerificationEvaluator(new MockVerificationCheckRunner());

        VerificationResult result = await evaluator.VerifyAsync("inc-001", [], CancellationToken.None);

        Assert.Equal(VerificationOutcome.Inconclusive, result.Outcome);
        Assert.Empty(result.CheckResults);
    }

    [Fact]
    public async Task Verify_AllChecksPass_Passes()
    {
        var runner = new MockVerificationCheckRunner();
        runner.SetActualValue("http://sample-api/api/orders", "200");
        var evaluator = new VerificationEvaluator(runner);

        VerificationResult result = await evaluator.VerifyAsync("inc-001", [Step()], CancellationToken.None);

        Assert.Equal(VerificationOutcome.Passed, result.Outcome);
        Assert.True(Assert.Single(result.CheckResults).Passed);
    }

    [Fact]
    public async Task Verify_AnyFailedCheck_Fails()
    {
        var runner = new MockVerificationCheckRunner();
        runner.SetActualValue("http://sample-api/api/orders", "200");
        runner.SetActualValue("http://sample-api/healthz", "503");
        var evaluator = new VerificationEvaluator(runner);

        VerificationResult result = await evaluator.VerifyAsync(
            "inc-001",
            [Step(), Step(target: "http://sample-api/healthz")],
            CancellationToken.None);

        Assert.Equal(VerificationOutcome.Failed, result.Outcome);
        Assert.Equal(2, result.CheckResults.Count);
    }

    [Fact]
    public async Task Verify_UnconfiguredTarget_FailsInsteadOfGuessing()
    {
        var evaluator = new VerificationEvaluator(new MockVerificationCheckRunner());

        VerificationResult result = await evaluator.VerifyAsync("inc-001", [Step()], CancellationToken.None);

        Assert.Equal(VerificationOutcome.Failed, result.Outcome);
        Assert.Equal("no result configured", result.CheckResults[0].ActualValue);
    }
}
