using AzureAgenticOps.Tier1SreAgent;

namespace UnitTests;

/// <summary>Tests for the deterministic Insights retrieval capability.</summary>
public sealed class InsightsCapabilityTests
{
    private static InsightsCapability CreateCapability() =>
        new(KnowledgeBase.LoadFromFile(
            Path.Combine(ScenarioLoader.RepositoryRoot, "knowledge", "knowledge-base.json")));

    [Fact]
    public void Search_KnownRoutingScenario_ReturnsRoutingRunbookFirst()
    {
        ScenarioLoader.Scenario scenario = ScenarioLoader.Load("001-known-routing-error");

        InsightsResult result = CreateCapability().Search(scenario.Incident, scenario.Evidence);

        Assert.NotEmpty(result.Hits);
        Assert.Equal("runbook-routing-config", result.Hits[0].Entry.EntryId);
        Assert.NotEmpty(result.Hits[0].MatchedKeywords);
    }

    [Fact]
    public void Search_Ambiguous404Scenario_Returns404TriageRunbook()
    {
        ScenarioLoader.Scenario scenario = ScenarioLoader.Load("002-ambiguous-404-increase");

        InsightsResult result = CreateCapability().Search(scenario.Incident, scenario.Evidence);

        Assert.Contains(result.Hits, hit => hit.Entry.EntryId == "runbook-404-triage");
    }

    [Fact]
    public void Search_ResultsAreOrderedByDescendingMatchCount()
    {
        ScenarioLoader.Scenario scenario = ScenarioLoader.Load("001-known-routing-error");

        InsightsResult result = CreateCapability().Search(scenario.Incident, scenario.Evidence);

        int[] counts = result.Hits.Select(hit => hit.MatchedKeywords.Count).ToArray();
        Assert.Equal(counts.OrderByDescending(count => count), counts);
    }
}
