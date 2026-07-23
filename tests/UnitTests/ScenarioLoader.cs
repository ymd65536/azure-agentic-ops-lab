using AzureAgenticOps.Contracts;

namespace UnitTests;

/// <summary>
/// Loads scenario fixtures from the version-controlled <c>scenarios</c> directory.
/// </summary>
public static class ScenarioLoader
{
    /// <summary>The expected rule evaluation outcome recorded for a scenario.</summary>
    /// <param name="Classification">The expected classification.</param>
    /// <param name="MatchedPatternName">The expected matched pattern name, when a rule should match.</param>
    /// <param name="MinimumConfidence">The minimum acceptable confidence.</param>
    /// <param name="RecommendedDisposition">The expected disposition.</param>
    /// <param name="EscalateToTier2">Whether Tier 2 escalation is expected.</param>
    /// <param name="ProposedActionType">The expected proposed action type, when applicable.</param>
    public sealed record ExpectedClassification(
        IncidentClassification Classification,
        string? MatchedPatternName,
        double MinimumConfidence,
        AgentDisposition RecommendedDisposition,
        bool EscalateToTier2,
        string? ProposedActionType);

    /// <summary>The expected end-to-end outcome recorded for a scenario.</summary>
    /// <param name="FinalState">The expected final workflow state.</param>
    /// <param name="Tier2Invoked">Whether Tier 2 is expected to be invoked.</param>
    /// <param name="RequiresApproval">Whether human approval is expected to be required.</param>
    /// <param name="MaxActionAttempts">The maximum expected remediation attempts.</param>
    /// <param name="ExpectedActionTypes">The action types expected in the plan.</param>
    /// <param name="VerificationOutcome">The expected verification outcome.</param>
    /// <param name="Notes">Free-form notes about the expected outcome.</param>
    public sealed record ExpectedResult(
        string FinalState,
        bool Tier2Invoked,
        bool RequiresApproval,
        int MaxActionAttempts,
        IReadOnlyList<string> ExpectedActionTypes,
        string VerificationOutcome,
        string Notes);

    /// <summary>A fully loaded scenario.</summary>
    /// <param name="Incident">The incident contract.</param>
    /// <param name="Evidence">All evidence items in the scenario, ordered by evidence identifier.</param>
    /// <param name="ExpectedClassificationResult">The expected rule evaluation outcome.</param>
    /// <param name="ExpectedFinalResult">The expected end-to-end outcome.</param>
    public sealed record Scenario(
        Incident Incident,
        IReadOnlyList<IncidentEvidence> Evidence,
        ExpectedClassification ExpectedClassificationResult,
        ExpectedResult ExpectedFinalResult);

    /// <summary>Loads a scenario by its directory name, for example "001-known-routing-error".</summary>
    /// <param name="scenarioName">The scenario directory name.</param>
    /// <returns>The loaded scenario.</returns>
    public static Scenario Load(string scenarioName)
    {
        string scenarioDirectory = Path.Combine(FindRepositoryRoot(), "scenarios", scenarioName);
        if (!Directory.Exists(scenarioDirectory))
        {
            throw new DirectoryNotFoundException($"Scenario directory not found: {scenarioDirectory}");
        }

        Incident incident = ReadContract<Incident>(Path.Combine(scenarioDirectory, "incident.json"));
        ExpectedClassification expectedClassification =
            ReadContract<ExpectedClassification>(Path.Combine(scenarioDirectory, "expected-classification.json"));
        ExpectedResult expectedResult =
            ReadContract<ExpectedResult>(Path.Combine(scenarioDirectory, "expected-result.json"));

        IncidentEvidence[] evidence = Directory
            .EnumerateFiles(Path.Combine(scenarioDirectory, "evidence"), "*.json")
            .Select(ReadContract<IncidentEvidence>)
            .OrderBy(item => item.EvidenceId, StringComparer.Ordinal)
            .ToArray();

        return new Scenario(incident, evidence, expectedClassification, expectedResult);
    }

    private static T ReadContract<T>(string path) =>
        ContractSerialization.Deserialize<T>(File.ReadAllText(path));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AzureAgenticOps.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Repository root containing AzureAgenticOps.slnx was not found.");
    }
}
