using AzureAgenticOps.AgentRuntime;

namespace UnitTests;

/// <summary>Tests for <see cref="FilePromptStore"/> over the version-controlled prompts directory.</summary>
public sealed class PromptStoreTests
{
    private static FilePromptStore CreateStore() =>
        new(Path.Combine(ScenarioLoader.RepositoryRoot, "prompts"));

    [Theory]
    [InlineData("tier1-investigation")]
    [InlineData("tier2-remediation")]
    public void Load_ReturnsVersionControlledPrompt(string promptName)
    {
        PromptDefinition prompt = CreateStore().Load(promptName, "1.0");

        Assert.Equal(promptName, prompt.Name);
        Assert.Equal("1.0", prompt.Version);
        Assert.False(string.IsNullOrWhiteSpace(prompt.Content));
        Assert.Contains("JSON", prompt.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_UnknownPrompt_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => CreateStore().Load("missing-prompt", "1.0"));
    }

    [Fact]
    public void Load_PathTraversal_IsRejected()
    {
        FilePromptStore store = CreateStore();
        Assert.Throws<ArgumentException>(() => store.Load("../scenarios", "1.0"));
        Assert.Throws<ArgumentException>(() => store.Load("tier1-investigation", "../1.0"));
    }
}
