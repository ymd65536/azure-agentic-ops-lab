namespace AzureAgenticOps.AgentRuntime;

/// <summary>
/// A request to generate a structured response from a language model.
/// </summary>
/// <param name="PromptName">The name of the version-controlled prompt.</param>
/// <param name="PromptVersion">The version of the prompt.</param>
/// <param name="SystemPrompt">The system prompt content.</param>
/// <param name="UserInput">The user input content, typically serialized structured evidence.</param>
/// <param name="ModelId">The requested model identifier, when the caller pins a model.</param>
/// <param name="CorrelationId">The correlation identifier for observability.</param>
public sealed record AgentModelRequest(
    string PromptName,
    string PromptVersion,
    string SystemPrompt,
    string UserInput,
    string? ModelId = null,
    string? CorrelationId = null);

/// <summary>
/// Token usage reported by a model invocation, when available.
/// </summary>
/// <param name="InputTokens">The number of input tokens, when reported.</param>
/// <param name="OutputTokens">The number of output tokens, when reported.</param>
public sealed record ModelUsage(
    int? InputTokens,
    int? OutputTokens);

/// <summary>
/// Observability metadata captured for every model invocation.
/// </summary>
/// <param name="PromptName">The prompt name used for the invocation.</param>
/// <param name="PromptVersion">The prompt version used for the invocation.</param>
/// <param name="ModelId">The identifier of the model that produced the response.</param>
/// <param name="Duration">The total invocation duration.</param>
/// <param name="Usage">Token usage, when available.</param>
/// <param name="ValidationSucceeded">Whether structured response validation succeeded.</param>
/// <param name="RetryCount">The number of retries performed before the final outcome.</param>
public sealed record ModelInvocationMetadata(
    string PromptName,
    string PromptVersion,
    string ModelId,
    TimeSpan Duration,
    ModelUsage? Usage,
    bool ValidationSucceeded,
    int RetryCount);

/// <summary>
/// A validated structured response from a language model.
/// </summary>
/// <typeparam name="T">The structured response contract type.</typeparam>
/// <param name="Value">The validated structured value.</param>
/// <param name="Metadata">Observability metadata for the invocation.</param>
public sealed record AgentModelResponse<T>(
    T Value,
    ModelInvocationMetadata Metadata);

/// <summary>
/// Thrown when a model response cannot be validated against the expected
/// structured contract. Invalid output must never be passed downstream.
/// </summary>
public sealed class ModelResponseValidationException : Exception
{
    /// <summary>Initializes a new instance of the exception.</summary>
    /// <param name="message">The validation failure description.</param>
    public ModelResponseValidationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the exception.</summary>
    /// <param name="message">The validation failure description.</param>
    /// <param name="innerException">The underlying parsing or validation error.</param>
    public ModelResponseValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Abstraction over language model access. All model calls in the system must go
/// through this interface so that agents can be tested deterministically and the
/// underlying provider can change without touching agent logic.
/// </summary>
public interface IAgentModelClient
{
    /// <summary>
    /// Generates a structured response from the model and validates it against
    /// the contract type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The structured response contract type.</typeparam>
    /// <param name="request">The model request.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The validated structured response with invocation metadata.</returns>
    /// <exception cref="ModelResponseValidationException">The model output could not be validated.</exception>
    Task<AgentModelResponse<T>> GenerateStructuredResponseAsync<T>(
        AgentModelRequest request,
        CancellationToken cancellationToken);
}
