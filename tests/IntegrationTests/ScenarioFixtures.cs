using AzureAgenticOps.Contracts;

namespace IntegrationTests;

/// <summary>
/// Loads scenario fixtures from the version-controlled <c>scenarios</c> directory.
/// </summary>
public static class ScenarioFixtures
{
    /// <summary>Gets the repository root directory.</summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>Loads the incident contract for a scenario.</summary>
    /// <param name="scenarioName">The scenario directory name.</param>
    /// <returns>The incident.</returns>
    public static Incident LoadIncident(string scenarioName) =>
        ReadContract<Incident>(Path.Combine(RepositoryRoot, "scenarios", scenarioName, "incident.json"));

    /// <summary>Loads the evidence items for a scenario.</summary>
    /// <param name="scenarioName">The scenario directory name.</param>
    /// <returns>The evidence items, ordered by file name.</returns>
    public static IReadOnlyList<IncidentEvidence> LoadEvidence(string scenarioName) =>
        Directory.GetFiles(Path.Combine(RepositoryRoot, "scenarios", scenarioName, "evidence"), "*.json")
            .Order(StringComparer.Ordinal)
            .Select(ReadContract<IncidentEvidence>)
            .ToList();

    private static T ReadContract<T>(string path) =>
        ContractSerialization.Deserialize<T>(File.ReadAllText(path));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "scenarios")) &&
                File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root with a 'scenarios' directory was not found.");
    }
}
