using System.Text.Json;
using AzureAgenticOps.Contracts;

namespace AzureAgenticOps.AgentRuntime;

/// <summary>
/// A deterministic in-memory model client for tests. Behaviors are enqueued in
/// order and consumed one per invocation, allowing tests to control response
/// content, latency, failures, and invalid JSON output without any network access.
/// </summary>
public sealed class FakeAgentModelClient : IAgentModelClient
{
    private readonly Queue<FakeModelBehavior> _behaviors = new();
    private readonly Lock _lock = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the fake client.</summary>
    /// <param name="timeProvider">The time provider used for simulated latency. Defaults to <see cref="TimeProvider.System"/>.</param>
    public FakeAgentModelClient(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Gets the number of invocations performed so far.</summary>
    public int InvocationCount { get; private set; }

    /// <summary>Gets the requests received so far, in order.</summary>
    public IReadOnlyList<AgentModelRequest> ReceivedRequests => _receivedRequests;

    private readonly List<AgentModelRequest> _receivedRequests = [];

    /// <summary>
    /// Enqueues a successful response. The value is serialized with the canonical
    /// contract serializer and parsed back on invocation, mirroring a real model round trip.
    /// </summary>
    /// <typeparam name="T">The structured response type.</typeparam>
    /// <param name="value">The value to return.</param>
    /// <param name="delay">Optional simulated latency before the response is produced.</param>
    /// <param name="usage">Optional token usage to report.</param>
    public void EnqueueResponse<T>(T value, TimeSpan? delay = null, ModelUsage? usage = null)
    {
        string json = ContractSerialization.Serialize(value);
        Enqueue(new FakeModelBehavior(json, null, delay ?? TimeSpan.Zero, usage));
    }

    /// <summary>
    /// Enqueues raw model output. Use this to simulate invalid JSON or JSON that
    /// does not conform to the expected contract.
    /// </summary>
    /// <param name="rawOutput">The raw text the fake model will return.</param>
    /// <param name="delay">Optional simulated latency before the response is produced.</param>
    public void EnqueueRawOutput(string rawOutput, TimeSpan? delay = null)
    {
        Enqueue(new FakeModelBehavior(rawOutput, null, delay ?? TimeSpan.Zero, null));
    }

    /// <summary>
    /// Enqueues a failure. The exception is thrown when the corresponding invocation occurs.
    /// </summary>
    /// <param name="exception">The exception to throw.</param>
    /// <param name="delay">Optional simulated latency before the failure occurs.</param>
    public void EnqueueFailure(Exception exception, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Enqueue(new FakeModelBehavior(null, exception, delay ?? TimeSpan.Zero, null));
    }

    /// <inheritdoc />
    public async Task<AgentModelResponse<T>> GenerateStructuredResponseAsync<T>(
        AgentModelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        FakeModelBehavior behavior;
        lock (_lock)
        {
            if (_behaviors.Count == 0)
            {
                throw new InvalidOperationException(
                    "FakeAgentModelClient has no enqueued behavior. Enqueue a response, raw output, or failure before invoking.");
            }

            behavior = _behaviors.Dequeue();
            InvocationCount++;
            _receivedRequests.Add(request);
        }

        long startTimestamp = _timeProvider.GetTimestamp();

        if (behavior.Delay > TimeSpan.Zero)
        {
            await Task.Delay(behavior.Delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (behavior.Exception is not null)
        {
            throw behavior.Exception;
        }

        T value;
        try
        {
            value = ContractSerialization.Deserialize<T>(behavior.RawOutput!);
        }
        catch (JsonException exception)
        {
            throw new ModelResponseValidationException(
                $"Fake model output could not be parsed as '{typeof(T).Name}'.", exception);
        }

        TimeSpan duration = _timeProvider.GetElapsedTime(startTimestamp);
        var metadata = new ModelInvocationMetadata(
            request.PromptName,
            request.PromptVersion,
            request.ModelId ?? "fake-model",
            duration,
            behavior.Usage,
            ValidationSucceeded: true,
            RetryCount: 0);

        return new AgentModelResponse<T>(value, metadata);
    }

    private void Enqueue(FakeModelBehavior behavior)
    {
        lock (_lock)
        {
            _behaviors.Enqueue(behavior);
        }
    }

    private sealed record FakeModelBehavior(
        string? RawOutput,
        Exception? Exception,
        TimeSpan Delay,
        ModelUsage? Usage);
}
