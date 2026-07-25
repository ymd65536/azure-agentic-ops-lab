namespace AzureAgenticOps.AgentRuntime;

/// <summary>
/// A version-controlled prompt loaded from the <c>prompts</c> directory.
/// </summary>
/// <param name="Name">The prompt name.</param>
/// <param name="Version">The prompt version.</param>
/// <param name="Content">The prompt content.</param>
public sealed record PromptDefinition(
    string Name,
    string Version,
    string Content);

/// <summary>
/// Provides access to version-controlled prompts. Prompts must never be embedded
/// in application source code; they are stored as files under <c>prompts</c>.
/// </summary>
public interface IPromptStore
{
    /// <summary>Loads a prompt by name and version.</summary>
    /// <param name="name">The prompt name.</param>
    /// <param name="version">The prompt version.</param>
    /// <returns>The prompt definition.</returns>
    /// <exception cref="FileNotFoundException">The prompt does not exist.</exception>
    PromptDefinition Load(string name, string version);
}

/// <summary>
/// Loads prompts from a directory laid out as <c>&lt;root&gt;/&lt;name&gt;/&lt;version&gt;.md</c>.
/// The layout keeps every prompt version reviewable and diffable in source control.
/// </summary>
public sealed class FilePromptStore : IPromptStore
{
    private readonly string _rootDirectory;

    /// <summary>Initializes a new store rooted at the supplied directory.</summary>
    /// <param name="rootDirectory">The prompts root directory.</param>
    public FilePromptStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = rootDirectory;
    }

    /// <inheritdoc />
    public PromptDefinition Load(string name, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (name.Contains("..", StringComparison.Ordinal) || version.Contains("..", StringComparison.Ordinal) ||
            name.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            version.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            name.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            version.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("Prompt name and version must not contain path segments.");
        }

        string path = Path.Combine(_rootDirectory, name, $"{version}.md");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Prompt '{name}' version '{version}' was not found.", path);
        }

        return new PromptDefinition(name, version, File.ReadAllText(path));
    }
}
