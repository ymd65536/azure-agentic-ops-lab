using System.Net;
using System.Text;
using System.Text.Json;
using AzureAgenticOps.AgentRuntime;

namespace UnitTests;

/// <summary>
/// Tests for the Foundry chat-completion transport using a scripted HTTP
/// handler. No network access occurs.
/// </summary>
public sealed class FoundryChatCompletionTransportTests
{
    private static readonly ChatCompletionRequest Request = new(
        "demo-model", "system prompt", "user input", "corr-1");

    private static RemoteModelOptions Options(string endpoint = "https://models.example.invalid/api", string? apiVersion = null) => new()
    {
        Endpoint = endpoint,
        ModelId = "demo-model",
        ApiVersion = apiVersion,
    };

    private sealed class StaticAuthenticator : IChatCompletionAuthenticator
    {
        public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("api-key", "test-key");
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _respond;

        public ScriptedHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _respond(request, LastRequestBody ?? string.Empty);
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static FoundryChatCompletionTransport Transport(ScriptedHandler handler, RemoteModelOptions? options = null) =>
        new(new HttpClient(handler), options ?? Options(), new StaticAuthenticator());

    [Fact]
    public async Task Complete_SuccessResponse_ReturnsContentModelAndUsage()
    {
        var handler = new ScriptedHandler((_, _) => JsonResponse(HttpStatusCode.OK, """
            {"model":"demo-model-v2","choices":[{"message":{"role":"assistant","content":"{\"ok\":true}"}}],
             "usage":{"prompt_tokens":120,"completion_tokens":45}}
            """));

        ChatCompletionResult result = await Transport(handler).CompleteAsync(Request, CancellationToken.None);

        Assert.Equal("""{"ok":true}""", result.Content);
        Assert.Equal("demo-model-v2", result.ModelId);
        Assert.Equal(120, result.InputTokens);
        Assert.Equal(45, result.OutputTokens);
    }

    [Fact]
    public async Task Complete_SendsPromptsModelAndJsonResponseFormat()
    {
        var handler = new ScriptedHandler((_, _) => JsonResponse(HttpStatusCode.OK, """
            {"choices":[{"message":{"content":"{}"}}]}
            """));

        await Transport(handler).CompleteAsync(Request, CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("demo-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("system", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("system prompt", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal("user input", body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("test-key", handler.LastRequest!.Headers.GetValues("api-key").Single());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Complete_TransientStatus_ThrowsTransientTransportException(HttpStatusCode statusCode)
    {
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(statusCode));

        await Assert.ThrowsAsync<TransientTransportException>(() =>
            Transport(handler).CompleteAsync(Request, CancellationToken.None));
    }

    [Fact]
    public async Task Complete_NonRetryableStatus_ThrowsWithoutBodyContents()
    {
        var handler = new ScriptedHandler((_, _) =>
            JsonResponse(HttpStatusCode.Unauthorized, """{"error":"secret-diagnostic"}"""));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Transport(handler).CompleteAsync(Request, CancellationToken.None));

        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-diagnostic", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Complete_NetworkFailure_ThrowsTransientTransportException()
    {
        var handler = new ScriptedHandler((_, _) => throw new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<TransientTransportException>(() =>
            Transport(handler).CompleteAsync(Request, CancellationToken.None));
    }

    [Fact]
    public async Task Complete_MissingChoices_ThrowsInvalidOperation()
    {
        var handler = new ScriptedHandler((_, _) => JsonResponse(HttpStatusCode.OK, """{"choices":[]}"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Transport(handler).CompleteAsync(Request, CancellationToken.None));
    }

    [Theory]
    [InlineData("https://models.example.invalid/api", null, "https://models.example.invalid/api/chat/completions")]
    [InlineData("https://models.example.invalid/api/chat/completions", null, "https://models.example.invalid/api/chat/completions")]
    [InlineData("https://models.example.invalid/openai/deployments/gpt", "2024-06-01", "https://models.example.invalid/openai/deployments/gpt/chat/completions?api-version=2024-06-01")]
    public void BuildRequestUri_AppendsPathAndApiVersion(string endpoint, string? apiVersion, string expected)
    {
        Assert.Equal(new Uri(expected), FoundryChatCompletionTransport.BuildRequestUri(endpoint, apiVersion));
    }
}
