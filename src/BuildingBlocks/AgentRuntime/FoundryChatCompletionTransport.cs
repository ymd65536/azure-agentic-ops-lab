using System.Net;
using System.Text;
using System.Text.Json;

namespace AzureAgenticOps.AgentRuntime;

/// <summary>
/// Applies authentication to an outgoing chat-completion HTTP request. The
/// transport never sees raw credential material directly; it delegates to an
/// authenticator so that credential acquisition (DefaultAzureCredential tokens
/// or secret-store-resolved API keys) stays outside the wire-protocol code.
/// Implementations must never log the credential value.
/// </summary>
public interface IChatCompletionAuthenticator
{
    /// <summary>Applies authentication headers to the request.</summary>
    /// <param name="request">The outgoing HTTP request.</param>
    /// <param name="cancellationToken">A token to cancel credential acquisition.</param>
    Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}

/// <summary>
/// Provides bearer access tokens for the model endpoint. The implementation is
/// registered by the host (for example one backed by
/// <c>DefaultAzureCredential</c>) so that this building block stays free of
/// provider SDK dependencies.
/// </summary>
public interface IAccessTokenProvider
{
    /// <summary>Gets a valid access token for the model endpoint.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The access token value.</returns>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Resolves named secrets through an abstraction such as the Dapr secret store
/// building block. Only secret names travel through configuration; values are
/// resolved at call time and never logged.
/// </summary>
public interface ISecretResolver
{
    /// <summary>Resolves the value of a named secret.</summary>
    /// <param name="secretName">The secret name.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The secret value.</returns>
    Task<string> GetSecretAsync(string secretName, CancellationToken cancellationToken);
}

/// <summary>
/// Authenticates requests with a bearer token acquired from an
/// <see cref="IAccessTokenProvider"/> (Microsoft Entra / DefaultAzureCredential path).
/// </summary>
public sealed class BearerTokenChatCompletionAuthenticator : IChatCompletionAuthenticator
{
    private readonly IAccessTokenProvider _tokenProvider;

    /// <summary>Initializes a new authenticator.</summary>
    /// <param name="tokenProvider">The access token provider.</param>
    public BearerTokenChatCompletionAuthenticator(IAccessTokenProvider tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _tokenProvider = tokenProvider;
    }

    /// <inheritdoc />
    public async Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string token = await _tokenProvider.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }
}

/// <summary>
/// Authenticates requests with an API key resolved by name through an
/// <see cref="ISecretResolver"/>. The key is sent in the <c>api-key</c> header
/// used by Azure OpenAI compatible endpoints.
/// </summary>
public sealed class ApiKeyChatCompletionAuthenticator : IChatCompletionAuthenticator
{
    private readonly ISecretResolver _secretResolver;
    private readonly string _secretName;

    /// <summary>Initializes a new authenticator.</summary>
    /// <param name="secretResolver">The secret resolver.</param>
    /// <param name="secretName">The name of the secret holding the API key.</param>
    public ApiKeyChatCompletionAuthenticator(ISecretResolver secretResolver, string secretName)
    {
        ArgumentNullException.ThrowIfNull(secretResolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        _secretResolver = secretResolver;
        _secretName = secretName;
    }

    /// <inheritdoc />
    public async Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string apiKey = await _secretResolver.GetSecretAsync(_secretName, cancellationToken).ConfigureAwait(false);
        request.Headers.Remove("api-key");
        request.Headers.Add("api-key", apiKey);
    }
}

/// <summary>
/// An <see cref="IChatCompletionTransport"/> for Microsoft Foundry and other
/// OpenAI-compatible chat-completions endpoints. The transport sends the system
/// and user prompts, requests JSON-constrained output, and maps provider
/// throttling and server errors to <see cref="TransientTransportException"/> so
/// that <see cref="RemoteAgentModelClient"/> can apply its bounded retry policy.
/// Credentials are applied by an <see cref="IChatCompletionAuthenticator"/> and
/// are never logged or included in exception messages.
/// </summary>
public sealed class FoundryChatCompletionTransport : IChatCompletionTransport
{
    private readonly HttpClient _httpClient;
    private readonly RemoteModelOptions _options;
    private readonly IChatCompletionAuthenticator _authenticator;

    /// <summary>Initializes a new transport.</summary>
    /// <param name="httpClient">The HTTP client used to reach the endpoint.</param>
    /// <param name="options">The remote model options (endpoint, model identifier, API version).</param>
    /// <param name="authenticator">The authenticator applying credentials to each request.</param>
    public FoundryChatCompletionTransport(
        HttpClient httpClient,
        RemoteModelOptions options,
        IChatCompletionAuthenticator authenticator)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(authenticator);
        _httpClient = httpClient;
        _options = options;
        _authenticator = authenticator;
    }

    /// <inheritdoc />
    public async Task<ChatCompletionResult> CompleteAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        Uri requestUri = BuildRequestUri(_options.Endpoint, _options.ApiVersion);
        string body = SerializeRequestBody(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            httpRequest.Headers.TryAddWithoutValidation("x-correlation-id", request.CorrelationId);
        }

        await _authenticator.ApplyAsync(httpRequest, cancellationToken).ConfigureAwait(false);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new TransientTransportException(
                "The chat-completion request failed with a network error.", exception);
        }

        using (response)
        {
            if (IsTransientStatus(response.StatusCode))
            {
                throw new TransientTransportException(
                    $"The chat-completion endpoint returned transient status {(int)response.StatusCode}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                // The response body is intentionally omitted: it may echo prompt
                // content and provider diagnostics that do not belong in logs.
                throw new InvalidOperationException(
                    $"The chat-completion endpoint returned non-retryable status {(int)response.StatusCode}.");
            }

            string payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseCompletion(payload, request.ModelId);
        }
    }

    /// <summary>
    /// Builds the request URI. When the configured endpoint already targets a
    /// chat-completions path it is used as-is; otherwise <c>chat/completions</c>
    /// is appended. An API version is added as a query parameter when configured.
    /// </summary>
    /// <param name="endpoint">The configured endpoint URL.</param>
    /// <param name="apiVersion">The optional API version query value.</param>
    /// <returns>The absolute request URI.</returns>
    public static Uri BuildRequestUri(string endpoint, string? apiVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var builder = new UriBuilder(endpoint);
        if (!builder.Path.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = builder.Path.TrimEnd('/') + "/chat/completions";
        }

        if (!string.IsNullOrWhiteSpace(apiVersion))
        {
            string versionQuery = "api-version=" + Uri.EscapeDataString(apiVersion);
            builder.Query = string.IsNullOrEmpty(builder.Query)
                ? versionQuery
                : builder.Query.TrimStart('?') + "&" + versionQuery;
        }

        return builder.Uri;
    }

    private static string SerializeRequestBody(ChatCompletionRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", request.ModelId);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            writer.WriteString("content", request.SystemPrompt);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", request.UserInput);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteStartObject("response_format");
            writer.WriteString("type", "json_object");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static ChatCompletionResult ParseCompletion(string payload, string requestedModelId)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("choices", out JsonElement choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0 ||
                !choices[0].TryGetProperty("message", out JsonElement message) ||
                !message.TryGetProperty("content", out JsonElement contentElement) ||
                contentElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException(
                    "The chat-completion response did not contain a message content choice.");
            }

            string content = contentElement.GetString() ?? string.Empty;
            string modelId = root.TryGetProperty("model", out JsonElement modelElement) &&
                             modelElement.ValueKind == JsonValueKind.String
                ? modelElement.GetString()!
                : requestedModelId;

            int? inputTokens = null;
            int? outputTokens = null;
            if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out JsonElement prompt) && prompt.TryGetInt32(out int promptTokens))
                {
                    inputTokens = promptTokens;
                }

                if (usage.TryGetProperty("completion_tokens", out JsonElement completion) && completion.TryGetInt32(out int completionTokens))
                {
                    outputTokens = completionTokens;
                }
            }

            return new ChatCompletionResult(content, modelId, inputTokens, outputTokens);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The chat-completion response was not valid JSON.", exception);
        }
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;
}
