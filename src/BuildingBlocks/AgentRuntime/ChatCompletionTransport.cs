namespace AzureAgenticOps.AgentRuntime;

/// <summary>
/// A single chat-completion request sent to a remote model transport. The
/// transport receives only prompt content; credentials, endpoints, and model
/// configuration live behind the transport implementation.
/// </summary>
/// <param name="ModelId">The model deployment or model identifier to invoke.</param>
/// <param name="SystemPrompt">The system prompt content.</param>
/// <param name="UserInput">The user input content, typically serialized structured evidence.</param>
/// <param name="CorrelationId">The correlation identifier for observability.</param>
public sealed record ChatCompletionRequest(
    string ModelId,
    string SystemPrompt,
    string UserInput,
    string? CorrelationId = null);

/// <summary>
/// The raw result of a chat completion. <see cref="Content"/> is expected to be
/// a JSON document conforming to a versioned contract; the caller validates it
/// before anything is passed downstream.
/// </summary>
/// <param name="Content">The raw model output text.</param>
/// <param name="ModelId">The identifier of the model that produced the output.</param>
/// <param name="InputTokens">Input token count, when reported by the provider.</param>
/// <param name="OutputTokens">Output token count, when reported by the provider.</param>
public sealed record ChatCompletionResult(
    string Content,
    string ModelId,
    int? InputTokens = null,
    int? OutputTokens = null);

/// <summary>
/// Thrown by a transport for failures that are safe to retry, such as transient
/// network errors or provider throttling. Non-transient failures must use other
/// exception types and are not retried.
/// </summary>
public sealed class TransientTransportException : Exception
{
    /// <summary>Initializes a new instance of the exception.</summary>
    /// <param name="message">The transient failure description.</param>
    public TransientTransportException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the exception.</summary>
    /// <param name="message">The transient failure description.</param>
    /// <param name="innerException">The underlying transport error.</param>
    public TransientTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Abstraction over the remote model wire protocol. Agent code and
/// <see cref="RemoteAgentModelClient"/> depend only on this interface so that no
/// agent implementation references the Microsoft Foundry SDK directly. The
/// concrete transport owns endpoint resolution, authentication
/// (DefaultAzureCredential or a secret-store-referenced API key), and the
/// provider request format.
/// </summary>
public interface IChatCompletionTransport
{
    /// <summary>Sends a chat-completion request and returns the raw result.</summary>
    /// <param name="request">The completion request.</param>
    /// <param name="cancellationToken">A token to cancel the operation, including per-attempt timeouts.</param>
    /// <returns>The raw completion result.</returns>
    Task<ChatCompletionResult> CompleteAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// A transport placeholder registered when no real transport is configured.
/// Every invocation fails with a clear message instead of guessing at a provider
/// API. The failure is safe: in Shadow mode it is recorded as an evaluation
/// error without affecting the deterministic workflow, and in RemoteModel mode
/// it surfaces as a bounded model failure.
/// </summary>
/// <remarks>
/// TODO: Replace with a Microsoft Foundry transport once the SDK and API surface
/// for the target model endpoints are confirmed. The implementation must:
/// build the client from <see cref="RemoteModelOptions.Endpoint"/> and
/// <see cref="RemoteModelOptions.ModelId"/>, authenticate with
/// <c>DefaultAzureCredential</c> (or resolve
/// <see cref="RemoteModelOptions.ApiKeySecretName"/> through the secret store
/// abstraction), request JSON-constrained structured output, propagate the
/// <see cref="CancellationToken"/>, and surface retryable provider errors as
/// <see cref="TransientTransportException"/>. It must never log credentials.
/// </remarks>
public sealed class UnconfiguredChatCompletionTransport : IChatCompletionTransport
{
    /// <inheritdoc />
    public Task<ChatCompletionResult> CompleteAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            "No remote model transport is configured. Register an IChatCompletionTransport " +
            "implementation for the Microsoft Foundry endpoint before using RemoteModel or Shadow mode.");
    }
}
