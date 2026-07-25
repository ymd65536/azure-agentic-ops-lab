using Azure.Core;
using Azure.Identity;
using AzureAgenticOps.AgentRuntime;

namespace AzureAgenticOps.IncidentApi;

/// <summary>
/// Provides bearer tokens for the model endpoint through
/// <see cref="DefaultAzureCredential"/>, so the same code path works with
/// developer credentials locally and Microsoft Entra Workload ID on AKS.
/// Token values are returned to the transport only and are never logged.
/// </summary>
public sealed class AzureIdentityAccessTokenProvider : IAccessTokenProvider
{
    private readonly TokenCredential _credential;
    private readonly string _scope;

    /// <summary>Initializes a new provider.</summary>
    /// <param name="scope">The token scope to request.</param>
    /// <param name="credential">The credential chain. Defaults to <see cref="DefaultAzureCredential"/>.</param>
    public AzureIdentityAccessTokenProvider(string scope, TokenCredential? credential = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        _scope = scope;
        _credential = credential ?? new DefaultAzureCredential();
    }

    /// <inheritdoc />
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        AccessToken token = await _credential
            .GetTokenAsync(new TokenRequestContext([_scope]), cancellationToken)
            .ConfigureAwait(false);
        return token.Token;
    }
}

/// <summary>
/// Resolves secrets through the Dapr secret store building block over the
/// sidecar HTTP API. Configuration carries only secret names; the value is
/// fetched at call time from the logical <c>secret-store</c> component and is
/// never logged.
/// </summary>
public sealed class DaprSecretStoreSecretResolver : ISecretResolver
{
    private readonly HttpClient _httpClient;
    private readonly int _daprHttpPort;
    private readonly string _secretStoreName;

    /// <summary>Initializes a new resolver.</summary>
    /// <param name="httpClient">The HTTP client used to reach the sidecar.</param>
    /// <param name="daprHttpPort">The Dapr sidecar HTTP port.</param>
    /// <param name="secretStoreName">The logical secret store component name.</param>
    public DaprSecretStoreSecretResolver(HttpClient httpClient, int daprHttpPort, string secretStoreName = "secret-store")
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretStoreName);
        _httpClient = httpClient;
        _daprHttpPort = daprHttpPort;
        _secretStoreName = secretStoreName;
    }

    /// <inheritdoc />
    public async Task<string> GetSecretAsync(string secretName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);

        var requestUri = new Uri(
            $"http://127.0.0.1:{_daprHttpPort}/v1.0/secrets/{Uri.EscapeDataString(_secretStoreName)}/{Uri.EscapeDataString(secretName)}");
        using HttpResponseMessage response = await _httpClient
            .GetAsync(requestUri, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Secret '{secretName}' could not be resolved from secret store '{_secretStoreName}' " +
                $"(status {(int)response.StatusCode}).");
        }

        // The Dapr response shape is {"<key>":"<value>"}; multi-value stores may
        // return several keys, in which case the requested name wins.
        using var document = await System.Text.Json.JsonDocument
            .ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.TryGetProperty(secretName, out System.Text.Json.JsonElement named) &&
            named.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return named.GetString()!;
        }

        foreach (System.Text.Json.JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return property.Value.GetString()!;
            }
        }

        throw new InvalidOperationException(
            $"Secret '{secretName}' from secret store '{_secretStoreName}' did not contain a string value.");
    }
}
