using AzureAgenticOps.Contracts;
using Microsoft.Extensions.Options;

namespace AzureAgenticOps.OpsConsole;

/// <summary>
/// A scenario fixture available to the console.
/// </summary>
/// <param name="Name">The scenario directory name, for example "001-known-routing-error".</param>
/// <param name="Incident">The incident fixture.</param>
/// <param name="Evidence">The evidence fixtures, ordered by evidence identifier.</param>
/// <param name="ExpectedFinalState">The final workflow state the scenario expects, when recorded.</param>
/// <param name="ExpectedNotes">Free-form notes describing the expected outcome, when recorded.</param>
public sealed record ScenarioFixture(
    string Name,
    Incident Incident,
    IReadOnlyList<IncidentEvidence> Evidence,
    string? ExpectedFinalState,
    string? ExpectedNotes);

/// <summary>
/// Loads the version-controlled scenario fixtures so the console can start a
/// scenario run without a shell script. The catalog is read once at startup;
/// fixtures are version controlled and do not change at runtime.
/// </summary>
public sealed class ScenarioCatalog
{
    private sealed record ExpectedResultFixture(string FinalState, string Notes);

    private readonly Lazy<IReadOnlyList<ScenarioFixture>> _scenarios;

    /// <summary>Initializes a new catalog.</summary>
    /// <param name="scenariosRoot">The directory holding the scenario fixtures.</param>
    public ScenarioCatalog(string scenariosRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenariosRoot);
        _scenarios = new Lazy<IReadOnlyList<ScenarioFixture>>(() => Load(scenariosRoot));
    }

    /// <summary>Gets the available scenarios, ordered by name.</summary>
    public IReadOnlyList<ScenarioFixture> Scenarios => _scenarios.Value;

    /// <summary>Gets a scenario by its directory name.</summary>
    /// <param name="name">The scenario directory name.</param>
    /// <returns>The scenario, or <see langword="null"/> when it is not available.</returns>
    public ScenarioFixture? Find(string name) =>
        Scenarios.FirstOrDefault(scenario => string.Equals(scenario.Name, name, StringComparison.Ordinal));

    private static IReadOnlyList<ScenarioFixture> Load(string scenariosRoot)
    {
        if (!Directory.Exists(scenariosRoot))
        {
            return [];
        }

        List<ScenarioFixture> scenarios = [];
        foreach (string directory in Directory.EnumerateDirectories(scenariosRoot).OrderBy(path => path, StringComparer.Ordinal))
        {
            string incidentPath = Path.Combine(directory, "incident.json");
            string evidenceDirectory = Path.Combine(directory, "evidence");
            if (!File.Exists(incidentPath))
            {
                continue;
            }

            Incident incident = ContractSerialization.Deserialize<Incident>(File.ReadAllText(incidentPath));
            IReadOnlyList<IncidentEvidence> evidence = Directory.Exists(evidenceDirectory)
                ? [.. Directory.EnumerateFiles(evidenceDirectory, "*.json")
                    .Select(path => ContractSerialization.Deserialize<IncidentEvidence>(File.ReadAllText(path)))
                    .OrderBy(item => item.EvidenceId, StringComparer.Ordinal)]
                : [];

            string expectedResultPath = Path.Combine(directory, "expected-result.json");
            ExpectedResultFixture? expected = File.Exists(expectedResultPath)
                ? ContractSerialization.Deserialize<ExpectedResultFixture>(File.ReadAllText(expectedResultPath))
                : null;

            scenarios.Add(new ScenarioFixture(
                Path.GetFileName(directory),
                incident,
                evidence,
                expected?.FinalState,
                expected?.Notes));
        }

        return scenarios;
    }
}

/// <summary>
/// Starts a scenario run against the IncidentApi: the fixture incident is given a
/// run-specific identifier so repeated runs never collide with the duplicate
/// protection of the API, the mock verification value is configured, and the
/// incident is submitted with its evidence.
/// </summary>
public sealed class ScenarioLauncher
{
    private readonly ScenarioCatalog _catalog;
    private readonly IncidentApiClient _client;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new launcher.</summary>
    /// <param name="catalog">The scenario catalog.</param>
    /// <param name="client">The IncidentApi client.</param>
    /// <param name="timeProvider">The time provider used to build unique incident identifiers.</param>
    public ScenarioLauncher(ScenarioCatalog catalog, IncidentApiClient client, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _catalog = catalog;
        _client = client;
        _timeProvider = timeProvider;
    }

    /// <summary>Starts a scenario run.</summary>
    /// <param name="scenarioName">The scenario directory name.</param>
    /// <param name="verificationValue">The value the mock verification runner reports for each affected service.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The identifier of the submitted incident.</returns>
    public async Task<string> StartAsync(
        string scenarioName,
        string verificationValue,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioName);
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationValue);

        ScenarioFixture scenario = _catalog.Find(scenarioName)
            ?? throw new InvalidOperationException($"Scenario '{scenarioName}' is not available.");

        string incidentId = $"{scenario.Incident.IncidentId}-{_timeProvider.GetUtcNow().ToUnixTimeSeconds()}";
        Incident incident = scenario.Incident with { IncidentId = incidentId };
        IReadOnlyList<IncidentEvidence> evidence =
            [.. scenario.Evidence.Select(item => item with { IncidentId = incidentId })];

        foreach (string service in incident.AffectedServices)
        {
            await _client
                .SetVerificationValueAsync($"demo/deployment/{service}", verificationValue, cancellationToken)
                .ConfigureAwait(false);
        }

        await _client.SubmitIncidentAsync(incident, evidence, cancellationToken).ConfigureAwait(false);
        return incidentId;
    }
}
