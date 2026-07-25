using AzureAgenticOps.AgentRuntime;
using AzureAgenticOps.Contracts;

namespace UnitTests;

/// <summary>
/// Tests for the remote model client using an in-memory transport. No network
/// access occurs; the transport is scripted per invocation.
/// </summary>
public sealed class RemoteAgentModelClientTests
{
    private static readonly AgentModelRequest Request = new(
        "tier1-investigation", "1.0", "system prompt", """{"incident":{}}""", CorrelationId: "corr-1");

    private static RemoteModelOptions Options(int timeoutSeconds = 5, int maxAttempts = 2) => new()
    {
        Endpoint = "https://example.invalid/models",
        ModelId = "demo-model",
        TimeoutSeconds = timeoutSeconds,
        MaxAttempts = maxAttempts,
    };

    private static InvestigationResult SampleResult() => new(
        SchemaVersions.V1,
        "inc-001",
        IncidentClassification.Known,
        "summary",
        [],
        [],
        0.9,
        AgentDisposition.Escalate,
        null,
        [],
        "reasoning");

    [Fact]
    public async Task GenerateStructuredResponse_ValidJson_IsDeserializedWithMetadata()
    {
        var transport = new ScriptedTransport(_ => new ChatCompletionResult(
            ContractSerialization.Serialize(SampleResult()), "demo-model-v2", 120, 45));
        var client = new RemoteAgentModelClient(transport, Options());

        AgentModelResponse<InvestigationResult> response =
            await client.GenerateStructuredResponseAsync<InvestigationResult>(Request, CancellationToken.None);

        Assert.Equal("inc-001", response.Value.IncidentId);
        Assert.Equal("demo-model-v2", response.Metadata.ModelId);
        Assert.Equal(120, response.Metadata.Usage?.InputTokens);
        Assert.Equal(45, response.Metadata.Usage?.OutputTokens);
        Assert.Equal(0, response.Metadata.RetryCount);
        Assert.True(response.Metadata.ValidationSucceeded);
    }

    [Fact]
    public async Task GenerateStructuredResponse_InvalidJson_ThrowsValidationException()
    {
        var transport = new ScriptedTransport(_ => new ChatCompletionResult("not json", "demo-model"));
        var client = new RemoteAgentModelClient(transport, Options());

        await Assert.ThrowsAsync<ModelResponseValidationException>(() =>
            client.GenerateStructuredResponseAsync<InvestigationResult>(Request, CancellationToken.None));
        Assert.Equal(1, transport.InvocationCount);
    }

    [Fact]
    public async Task GenerateStructuredResponse_TransientFailure_IsRetriedAndCounted()
    {
        int calls = 0;
        var transport = new ScriptedTransport(_ =>
        {
            calls++;
            if (calls == 1)
            {
                throw new TransientTransportException("throttled");
            }

            return new ChatCompletionResult(ContractSerialization.Serialize(SampleResult()), "demo-model");
        });
        var client = new RemoteAgentModelClient(transport, Options());

        AgentModelResponse<InvestigationResult> response =
            await client.GenerateStructuredResponseAsync<InvestigationResult>(Request, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(1, response.Metadata.RetryCount);
    }

    [Fact]
    public async Task GenerateStructuredResponse_ExhaustedTransientFailures_FailsSafely()
    {
        var transport = new ScriptedTransport(_ => throw new TransientTransportException("throttled"));
        var client = new RemoteAgentModelClient(transport, Options(maxAttempts: 2));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.GenerateStructuredResponseAsync<InvestigationResult>(Request, CancellationToken.None));
        Assert.Equal(2, transport.InvocationCount);
    }

    [Fact]
    public async Task GenerateStructuredResponse_CallerCancellation_IsPropagatedToTransport()
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken observed = CancellationToken.None;
        var transport = new ScriptedTransport(async (request, token) =>
        {
            observed = token;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return new ChatCompletionResult("unused", "demo-model");
        });
        var client = new RemoteAgentModelClient(transport, Options());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GenerateStructuredResponseAsync<InvestigationResult>(Request, cancellation.Token));
        Assert.True(observed.CanBeCanceled);
    }

    [Fact]
    public async Task GenerateStructuredResponse_UnconfiguredTransport_FailsWithClearMessage()
    {
        var client = new RemoteAgentModelClient(new UnconfiguredChatCompletionTransport(), Options());

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GenerateStructuredResponseAsync<InvestigationResult>(Request, CancellationToken.None));
        Assert.Contains("No remote model transport is configured", exception.Message);
    }

    /// <summary>A transport scripted per invocation for tests.</summary>
    internal sealed class ScriptedTransport : IChatCompletionTransport
    {
        private readonly Func<ChatCompletionRequest, CancellationToken, Task<ChatCompletionResult>> _behavior;

        public ScriptedTransport(Func<ChatCompletionRequest, ChatCompletionResult> behavior)
            : this((request, _) => Task.FromResult(behavior(request)))
        {
        }

        public ScriptedTransport(Func<ChatCompletionRequest, CancellationToken, Task<ChatCompletionResult>> behavior)
        {
            _behavior = behavior;
        }

        public int InvocationCount { get; private set; }

        public IReadOnlyList<ChatCompletionRequest> ReceivedRequests => _requests;

        private readonly List<ChatCompletionRequest> _requests = [];

        public Task<ChatCompletionResult> CompleteAsync(
            ChatCompletionRequest request,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            _requests.Add(request);
            return _behavior(request, cancellationToken);
        }
    }
}
