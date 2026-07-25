using AzureAgenticOps.Contracts;

namespace AzureAgenticOps.AgentRuntime;

/// <summary>
/// Typed options for the agent model runtime, bound from the <c>AgentRuntime</c>
/// configuration section. The default mode is <see cref="AgentExecutionMode.Deterministic"/>,
/// which performs no external communication.
/// </summary>
public sealed class AgentRuntimeOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "AgentRuntime";

    /// <summary>
    /// Gets or sets the execution mode name. Must be one of
    /// <c>Deterministic</c>, <c>RemoteModel</c>, or <c>Shadow</c> (case-insensitive).
    /// Unknown values are rejected at startup.
    /// </summary>
    public string Mode { get; set; } = nameof(AgentExecutionMode.Deterministic);

    /// <summary>Gets or sets the remote model options, required for RemoteModel and Shadow modes.</summary>
    public RemoteModelOptions RemoteModel { get; set; } = new();

    /// <summary>Gets or sets the shadow evaluation options, used in Shadow mode.</summary>
    public ShadowEvaluationOptions Shadow { get; set; } = new();

    /// <summary>
    /// Parses <see cref="Mode"/> into an <see cref="AgentExecutionMode"/>.
    /// </summary>
    /// <param name="mode">The parsed mode when parsing succeeds.</param>
    /// <returns><c>true</c> when the mode name is known; otherwise <c>false</c>.</returns>
    public bool TryGetMode(out AgentExecutionMode mode) =>
        Enum.TryParse(Mode, ignoreCase: true, out mode) && Enum.IsDefined(mode);

    /// <summary>
    /// Validates the options for startup. Unknown modes are rejected, and modes
    /// that reach a remote model require an endpoint and a model identifier.
    /// </summary>
    /// <param name="error">The validation failure description, when invalid.</param>
    /// <returns><c>true</c> when the options are valid; otherwise <c>false</c>.</returns>
    public bool Validate(out string? error)
    {
        if (!TryGetMode(out AgentExecutionMode mode))
        {
            error = $"AgentRuntime:Mode '{Mode}' is not a known execution mode. " +
                    "Valid values: Deterministic, RemoteModel, Shadow.";
            return false;
        }

        if (mode is AgentExecutionMode.RemoteModel or AgentExecutionMode.Shadow)
        {
            if (string.IsNullOrWhiteSpace(RemoteModel.Endpoint) ||
                string.IsNullOrWhiteSpace(RemoteModel.ModelId))
            {
                error = $"AgentRuntime:RemoteModel requires 'Endpoint' and 'ModelId' when Mode is '{mode}'.";
                return false;
            }

            if (!RemoteModelOptions.KnownAuthModes.Contains(RemoteModel.AuthMode, StringComparer.OrdinalIgnoreCase))
            {
                error = $"AgentRuntime:RemoteModel:AuthMode '{RemoteModel.AuthMode}' is not supported. " +
                        $"Valid values: {string.Join(", ", RemoteModelOptions.KnownAuthModes)}.";
                return false;
            }

            if (string.Equals(RemoteModel.AuthMode, RemoteModelOptions.ApiKeySecretReferenceAuthMode, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(RemoteModel.ApiKeySecretName))
            {
                error = "AgentRuntime:RemoteModel:ApiKeySecretName is required when AuthMode is 'ApiKeySecretReference'.";
                return false;
            }

            if (RemoteModel.TimeoutSeconds <= 0)
            {
                error = "AgentRuntime:RemoteModel:TimeoutSeconds must be positive.";
                return false;
            }
        }

        if (mode is AgentExecutionMode.Shadow && Shadow.TimeoutSeconds <= 0)
        {
            error = "AgentRuntime:Shadow:TimeoutSeconds must be positive.";
            return false;
        }

        error = null;
        return true;
    }
}

/// <summary>
/// Options for the remote model connection. Credentials are never stored here:
/// the default authentication mode is <c>DefaultAzureCredential</c>, and API-key
/// authentication references a secret name resolved through the Dapr secret
/// store abstraction, never a raw key value.
/// </summary>
public sealed class RemoteModelOptions
{
    /// <summary>Authentication through DefaultAzureCredential (Microsoft Entra Workload ID compatible).</summary>
    public const string DefaultAzureCredentialAuthMode = "DefaultAzureCredential";

    /// <summary>Authentication through an API key referenced by secret name.</summary>
    public const string ApiKeySecretReferenceAuthMode = "ApiKeySecretReference";

    /// <summary>The supported authentication mode names.</summary>
    public static readonly IReadOnlyList<string> KnownAuthModes =
        [DefaultAzureCredentialAuthMode, ApiKeySecretReferenceAuthMode];

    /// <summary>Gets or sets the model endpoint URL (for example a Microsoft Foundry model endpoint).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Gets or sets the model deployment or model identifier to invoke.</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the API version appended as an <c>api-version</c> query
    /// parameter, used by Azure OpenAI style endpoints. Optional.
    /// </summary>
    public string? ApiVersion { get; set; }

    /// <summary>
    /// Gets or sets the token scope requested when authenticating with
    /// <see cref="DefaultAzureCredentialAuthMode"/>. Defaults to the Cognitive
    /// Services scope used by Microsoft Foundry model endpoints.
    /// </summary>
    public string TokenScope { get; set; } = "https://cognitiveservices.azure.com/.default";

    /// <summary>Gets or sets the authentication mode. Defaults to DefaultAzureCredential.</summary>
    public string AuthMode { get; set; } = DefaultAzureCredentialAuthMode;

    /// <summary>
    /// Gets or sets the name of the secret holding the API key, resolved through
    /// the secret store abstraction. Only used when <see cref="AuthMode"/> is
    /// <see cref="ApiKeySecretReferenceAuthMode"/>. Never a raw key value.
    /// </summary>
    public string? ApiKeySecretName { get; set; }

    /// <summary>Gets or sets the per-attempt timeout in seconds. Defaults to 30 seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Gets or sets the maximum number of attempts per invocation, including the first. Defaults to 2.</summary>
    public int MaxAttempts { get; set; } = 2;
}

/// <summary>
/// Options for shadow evaluation runs.
/// </summary>
public sealed class ShadowEvaluationOptions
{
    /// <summary>Gets or sets the timeout in seconds for the shadow model invocation. Defaults to 30 seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the directory evaluation records are written to as JSON Lines.
    /// Defaults to <c>results/evaluations</c>.
    /// </summary>
    public string EvaluationOutputDirectory { get; set; } = Path.Combine("results", "evaluations");

    /// <summary>Gets or sets the scenario name recorded on evaluation records, when known.</summary>
    public string? ScenarioName { get; set; }
}
