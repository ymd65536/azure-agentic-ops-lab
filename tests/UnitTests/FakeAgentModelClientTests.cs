using AzureAgenticOps.AgentRuntime;
using AzureAgenticOps.Contracts;

namespace UnitTests;

public class FakeAgentModelClientTests
{
    private static AgentModelRequest CreateRequest() =>
        new("tier1/system", "1.0", "You are a Tier 1 SRE agent.", "{\"incidentId\":\"inc-001\"}", "fake-model", "corr-1");

    private static AgentHypothesis CreateHypothesis() =>
        new("Routing configuration removed the /api/orders route.", 0.9, ["ev-001-config"]);

    [Fact]
    public async Task EnqueuedResponse_IsReturnedWithMetadata()
    {
        var client = new FakeAgentModelClient();
        client.EnqueueResponse(CreateHypothesis(), usage: new ModelUsage(120, 45));

        AgentModelResponse<AgentHypothesis> response =
            await client.GenerateStructuredResponseAsync<AgentHypothesis>(CreateRequest(), CancellationToken.None);

        Assert.Equal(0.9, response.Value.Confidence);
        Assert.Equal("tier1/system", response.Metadata.PromptName);
        Assert.Equal("1.0", response.Metadata.PromptVersion);
        Assert.Equal("fake-model", response.Metadata.ModelId);
        Assert.True(response.Metadata.ValidationSucceeded);
        Assert.Equal(120, response.Metadata.Usage?.InputTokens);
        Assert.Equal(45, response.Metadata.Usage?.OutputTokens);
        Assert.Equal(1, client.InvocationCount);
    }

    [Fact]
    public async Task EnqueuedFailure_IsThrown()
    {
        var client = new FakeAgentModelClient();
        client.EnqueueFailure(new TimeoutException("model timed out"));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.GenerateStructuredResponseAsync<AgentHypothesis>(CreateRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task InvalidJsonOutput_ThrowsModelResponseValidationException()
    {
        var client = new FakeAgentModelClient();
        client.EnqueueRawOutput("this is not json {{{");

        await Assert.ThrowsAsync<ModelResponseValidationException>(() =>
            client.GenerateStructuredResponseAsync<AgentHypothesis>(CreateRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task SimulatedDelay_IsObservedUsingFakeTime()
    {
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var client = new FakeAgentModelClient(timeProvider);
        client.EnqueueResponse(CreateHypothesis(), delay: TimeSpan.FromSeconds(30));

        Task<AgentModelResponse<AgentHypothesis>> pending =
            client.GenerateStructuredResponseAsync<AgentHypothesis>(CreateRequest(), CancellationToken.None);

        Assert.False(pending.IsCompleted);
        timeProvider.Advance(TimeSpan.FromSeconds(30));

        AgentModelResponse<AgentHypothesis> response = await pending;
        Assert.True(response.Metadata.Duration >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task CancellationToken_IsPropagatedDuringDelay()
    {
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var client = new FakeAgentModelClient(timeProvider);
        client.EnqueueResponse(CreateHypothesis(), delay: TimeSpan.FromMinutes(5));
        using var cancellation = new CancellationTokenSource();

        Task<AgentModelResponse<AgentHypothesis>> pending =
            client.GenerateStructuredResponseAsync<AgentHypothesis>(CreateRequest(), cancellation.Token);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task AlreadyCancelledToken_ThrowsBeforeConsumingBehavior()
    {
        var client = new FakeAgentModelClient();
        client.EnqueueResponse(CreateHypothesis());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GenerateStructuredResponseAsync<AgentHypothesis>(CreateRequest(), cancellation.Token));

        Assert.Equal(0, client.InvocationCount);
    }

    [Fact]
    public async Task InvokingWithoutEnqueuedBehavior_Throws()
    {
        var client = new FakeAgentModelClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GenerateStructuredResponseAsync<AgentHypothesis>(CreateRequest(), CancellationToken.None));
    }
}
