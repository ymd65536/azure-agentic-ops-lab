using AzureAgenticOps.AgentRuntime;
using AzureAgenticOps.Contracts;
using AzureAgenticOps.IncidentApi;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntegrationTests;

/// <summary>
/// Tests that the AgentRuntime execution mode selects the model client
/// composition at startup: Deterministic by default (no external communication),
/// RemoteModel and Shadow when configured, and rejection of unknown modes.
/// </summary>
public sealed class AgentRuntimeModeSelectionTests
{
    private static WebApplicationFactory<Program> CreateFactory(
        params KeyValuePair<string, string?>[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(settings)));

    [Fact]
    public void DefaultConfiguration_UsesDeterministicStubOnly()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();

        var options = factory.Services.GetRequiredService<IOptions<AgentRuntimeOptions>>().Value;
        Assert.True(options.TryGetMode(out AgentExecutionMode mode));
        Assert.Equal(AgentExecutionMode.Deterministic, mode);
        Assert.IsType<DeterministicStubModelClient>(factory.Services.GetRequiredService<IAgentModelClient>());
    }

    [Fact]
    public void RemoteModelMode_UsesRemoteAgentModelClient()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(
            new("AgentRuntime:Mode", "RemoteModel"),
            new("AgentRuntime:RemoteModel:Endpoint", "https://example.invalid/models"),
            new("AgentRuntime:RemoteModel:ModelId", "demo-model"));

        Assert.IsType<RemoteAgentModelClient>(factory.Services.GetRequiredService<IAgentModelClient>());
    }

    [Fact]
    public void ShadowMode_WrapsDeterministicClientInShadowDecorator()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(
            new("AgentRuntime:Mode", "Shadow"),
            new("AgentRuntime:RemoteModel:Endpoint", "https://example.invalid/models"),
            new("AgentRuntime:RemoteModel:ModelId", "demo-model"));

        Assert.IsType<ShadowAgentModelClient>(factory.Services.GetRequiredService<IAgentModelClient>());
    }

    [Fact]
    public void UnknownMode_FailsStartupValidation()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(
            new KeyValuePair<string, string?>("AgentRuntime:Mode", "FullyAutonomous"));

        Assert.Throws<OptionsValidationException>(() => factory.Services.GetRequiredService<IAgentModelClient>());
    }

    [Fact]
    public void RemoteModeWithoutEndpoint_FailsStartupValidation()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(
            new KeyValuePair<string, string?>("AgentRuntime:Mode", "RemoteModel"));

        Assert.Throws<OptionsValidationException>(() => factory.Services.GetRequiredService<IAgentModelClient>());
    }
}
