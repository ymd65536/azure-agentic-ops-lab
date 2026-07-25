using AzureAgenticOps.Contracts;
using AzureAgenticOps.OpsConsole;

namespace UnitTests;

/// <summary>
/// Tests that the operations console loads the version-controlled scenario
/// fixtures it offers for a run.
/// </summary>
public sealed class ScenarioCatalogTests
{
    private static ScenarioCatalog CreateCatalog() =>
        new(Path.Combine(ScenarioLoader.RepositoryRoot, "scenarios"));

    [Fact]
    public void Catalog_LoadsEveryScenarioWithItsEvidence()
    {
        ScenarioCatalog catalog = CreateCatalog();

        Assert.NotEmpty(catalog.Scenarios);
        Assert.All(catalog.Scenarios, scenario =>
        {
            Assert.Equal(SchemaVersions.V1, scenario.Incident.SchemaVersion);
            Assert.NotEmpty(scenario.Evidence);
            Assert.All(scenario.Evidence, item => Assert.Equal(scenario.Incident.IncidentId, item.IncidentId));
            Assert.False(string.IsNullOrWhiteSpace(scenario.ExpectedFinalState));
        });
    }

    [Fact]
    public void Find_ReturnsTheRequestedScenarioAndNullForUnknownNames()
    {
        ScenarioCatalog catalog = CreateCatalog();

        ScenarioFixture? known = catalog.Find("001-known-routing-error");

        Assert.NotNull(known);
        Assert.Equal("inc-001", known.Incident.IncidentId);
        Assert.Null(catalog.Find("999-does-not-exist"));
    }

    [Fact]
    public void MissingScenariosDirectory_YieldsAnEmptyCatalog()
    {
        var catalog = new ScenarioCatalog(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"));

        Assert.Empty(catalog.Scenarios);
    }
}
