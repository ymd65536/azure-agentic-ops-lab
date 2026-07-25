using AzureAgenticOps.IncidentApi;

namespace IntegrationTests;

/// <summary>
/// Tests for the workflow hosting engine selection options.
/// </summary>
public sealed class WorkflowHostingOptionsTests
{
    [Theory]
    [InlineData("InProcess", true, false)]
    [InlineData("inprocess", true, false)]
    [InlineData("Dapr", true, true)]
    [InlineData("dapr", true, true)]
    [InlineData("Kubernetes", false, false)]
    [InlineData("", false, false)]
    public void Validate_AcceptsOnlyKnownEngines(string engine, bool expectedValid, bool expectedDapr)
    {
        var options = new WorkflowHostingOptions { Engine = engine };

        bool valid = options.Validate(out string? error);

        Assert.Equal(expectedValid, valid);
        Assert.Equal(expectedDapr, options.UsesDaprEngine);
        Assert.Equal(expectedValid, error is null);
    }

    [Fact]
    public void Default_IsInProcess()
    {
        var options = new WorkflowHostingOptions();

        Assert.Equal(WorkflowHostingOptions.InProcessEngine, options.Engine);
        Assert.False(options.UsesDaprEngine);
    }
}
