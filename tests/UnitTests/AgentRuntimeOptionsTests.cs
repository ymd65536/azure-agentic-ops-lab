using AzureAgenticOps.AgentRuntime;
using AzureAgenticOps.Contracts;

namespace UnitTests;

/// <summary>Tests for agent runtime options parsing and startup validation.</summary>
public sealed class AgentRuntimeOptionsTests
{
    [Theory]
    [InlineData("Deterministic", AgentExecutionMode.Deterministic)]
    [InlineData("deterministic", AgentExecutionMode.Deterministic)]
    [InlineData("RemoteModel", AgentExecutionMode.RemoteModel)]
    [InlineData("shadow", AgentExecutionMode.Shadow)]
    public void TryGetMode_KnownModes_AreParsedCaseInsensitively(string mode, AgentExecutionMode expected)
    {
        var options = new AgentRuntimeOptions { Mode = mode };

        Assert.True(options.TryGetMode(out AgentExecutionMode parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("Hybrid")]
    [InlineData("")]
    [InlineData("42")]
    public void Validate_UnknownMode_IsRejected(string mode)
    {
        var options = new AgentRuntimeOptions { Mode = mode };

        Assert.False(options.Validate(out string? error));
        Assert.Contains("not a known execution mode", error);
    }

    [Fact]
    public void Validate_DefaultOptions_AreDeterministicAndValid()
    {
        var options = new AgentRuntimeOptions();

        Assert.True(options.TryGetMode(out AgentExecutionMode mode));
        Assert.Equal(AgentExecutionMode.Deterministic, mode);
        Assert.True(options.Validate(out _));
    }

    [Theory]
    [InlineData("RemoteModel")]
    [InlineData("Shadow")]
    public void Validate_RemoteModes_RequireEndpointAndModelId(string mode)
    {
        var options = new AgentRuntimeOptions { Mode = mode };

        Assert.False(options.Validate(out string? error));
        Assert.Contains("Endpoint", error);

        options.RemoteModel.Endpoint = "https://example.invalid/models";
        options.RemoteModel.ModelId = "demo-model";
        Assert.True(options.Validate(out _));
    }

    [Fact]
    public void Validate_UnknownAuthMode_IsRejected()
    {
        var options = new AgentRuntimeOptions
        {
            Mode = "RemoteModel",
            RemoteModel =
            {
                Endpoint = "https://example.invalid/models",
                ModelId = "demo-model",
                AuthMode = "RawApiKeyInline",
            },
        };

        Assert.False(options.Validate(out string? error));
        Assert.Contains("AuthMode", error);
    }

    [Fact]
    public void RemoteModelOptions_CarryNoRawCredentialValue()
    {
        // Credentials are configured by secret reference or DefaultAzureCredential
        // only; the options type must not expose a raw key/credential property.
        var forbidden = new[] { "apikey", "key", "credential", "token", "password", "secretvalue" };
        IEnumerable<string> properties = typeof(RemoteModelOptions)
            .GetProperties()
            .Select(property => property.Name.ToLowerInvariant());

        foreach (string property in properties.Where(name => name != "apikeysecretname"))
        {
            Assert.DoesNotContain(property, forbidden);
        }
    }
}
