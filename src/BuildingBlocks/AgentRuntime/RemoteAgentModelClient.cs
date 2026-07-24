using System.Text.Json;
using AzureAgenticOps.Contracts;

namespace AzureAgenticOps.AgentRuntime;

/// <summary>
/// An <see cref="IAgentModelClient"/> that invokes a remote model through an
/// <see cref="IChatCompletionTransport"/>. The client owns per-attempt timeouts,
/// bounded retries of transient transport failures, structured-output
/// deserialization into existing contracts, and invocation metadata capture
/// (model identifier, latency, token usage, retry count). Invalid structured
/// output is surfaced as <see cref="ModelResponseValidationException"/> so the
/// caller's existing bounded repair path or safe failure applies; it is never
/// passed downstream. Because output is deserialized into closed contracts,
/// no output that could represent an arbitrary shell command is accepted.
/// </summary>
public sealed class RemoteAgentModelClient : IAgentModelClient
{
    private readonly IChatCompletionTransport _transport;
    private readonly RemoteModelOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new remote model client.</summary>
    /// <param name="transport">The wire-protocol transport.</param>
    /// <param name="options">The remote model options (model identifier, timeout, attempt bound).</param>
    /// <param name="timeProvider">The time provider. Defaults to <see cref="TimeProvider.System"/>.</param>
    public RemoteAgentModelClient(
        IChatCompletionTransport transport,
        RemoteModelOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(options);
        _transport = transport;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<AgentModelResponse<T>> GenerateStructuredResponseAsync<T>(
        AgentModelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        long startTimestamp = _timeProvider.GetTimestamp();
        var completionRequest = new ChatCompletionRequest(
            request.ModelId ?? _options.ModelId,
            request.SystemPrompt,
            request.UserInput,
            request.CorrelationId);

        int maxAttempts = Math.Max(1, _options.MaxAttempts);
        TimeSpan attemptTimeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        Exception? lastTransientFailure = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var timeoutSource = new CancellationTokenSource(attemptTimeout, _timeProvider);
            using CancellationTokenSource linkedSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

            ChatCompletionResult completion;
            try
            {
                completion = await _transport
                    .CompleteAsync(completionRequest, linkedSource.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception) when (timeoutSource.IsCancellationRequested)
            {
                lastTransientFailure = new TimeoutException(
                    $"Remote model invocation for prompt '{request.PromptName}' timed out after {attemptTimeout.TotalSeconds:0}s.",
                    exception);
                continue;
            }
            catch (TransientTransportException exception)
            {
                lastTransientFailure = exception;
                continue;
            }

            T value;
            try
            {
                value = ContractSerialization.Deserialize<T>(completion.Content);
            }
            catch (JsonException exception)
            {
                // Invalid structured output is not retried here; the caller's
                // bounded repair loop decides whether to re-prompt or fail safely.
                throw new ModelResponseValidationException(
                    $"Remote model output could not be validated as '{typeof(T).Name}' " +
                    $"for prompt '{request.PromptName}' v{request.PromptVersion}.",
                    exception);
            }

            var metadata = new ModelInvocationMetadata(
                request.PromptName,
                request.PromptVersion,
                completion.ModelId,
                _timeProvider.GetElapsedTime(startTimestamp),
                new ModelUsage(completion.InputTokens, completion.OutputTokens),
                ValidationSucceeded: true,
                RetryCount: attempt - 1);

            return new AgentModelResponse<T>(value, metadata);
        }

        throw new TimeoutException(
            $"Remote model invocation for prompt '{request.PromptName}' failed after {maxAttempts} attempt(s).",
            lastTransientFailure);
    }
}
