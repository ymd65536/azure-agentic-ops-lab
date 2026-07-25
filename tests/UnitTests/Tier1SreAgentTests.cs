using AzureAgenticOps.AgentRuntime;
using AzureAgenticOps.Contracts;
using AzureAgenticOps.Safety;
using AzureAgenticOps.Tier1SreAgent;

namespace UnitTests;

/// <summary>Tests for the Tier 1 agent's validation and deterministic guards.</summary>
public sealed class Tier1SreAgentTests
{
    private static readonly ScenarioLoader.Scenario Scenario = ScenarioLoader.Load("001-known-routing-error");

    private static Tier1SreAgent CreateAgent(FakeAgentModelClient modelClient, Tier1AgentOptions? options = null)
    {
        var promptStore = new FilePromptStore(Path.Combine(ScenarioLoader.RepositoryRoot, "prompts"));
        var insights = new InsightsCapability(KnowledgeBase.LoadFromFile(
            Path.Combine(ScenarioLoader.RepositoryRoot, "knowledge", "knowledge-base.json")));
        return new Tier1SreAgent(modelClient, promptStore, insights, options);
    }

    private static InvestigationResult ValidResult(
        double confidence = 0.9,
        AgentDisposition disposition = AgentDisposition.Resolve,
        RemediationAction? proposedAction = null)
    {
        proposedAction ??= new RemediationAction(
            ActionTypeCatalog.RollbackDemoDeployment,
            new ActionTarget("demo", "deployment", "sample-api"),
            new Dictionary<string, string>(),
            "inc-001-rollback-1");

        return new InvestigationResult(
            SchemaVersions.V1,
            Scenario.Incident.IncidentId,
            IncidentClassification.Known,
            "Routing configuration removed a route; rollback resolves the incident.",
            ["Route /api/orders removed in revision 43", "404 spike started immediately after deployment"],
            [new AgentHypothesis("Route removed by configuration deployment", 0.9, ["ev-001-config", "ev-001-log"])],
            confidence,
            disposition,
            proposedAction,
            [],
            "The 404 spike correlates with the routing configuration diff removing /api/orders.");
    }

    [Fact]
    public async Task Investigate_HighConfidenceKnownPattern_PassesThrough()
    {
        var modelClient = new FakeAgentModelClient();
        modelClient.EnqueueResponse(ValidResult());

        Tier1InvestigationOutcome outcome = await CreateAgent(modelClient)
            .InvestigateAsync(Scenario.Incident, Scenario.Evidence, "corr-1", CancellationToken.None);

        Assert.Equal(AgentDisposition.Resolve, outcome.Result.RecommendedDisposition);
        Assert.NotNull(outcome.Result.ProposedAction);
        Assert.NotEmpty(outcome.Insights.Hits);
        Assert.Equal("tier1-investigation", outcome.ModelMetadata.PromptName);
    }

    [Fact]
    public async Task Investigate_LowConfidenceResolve_IsEscalatedDeterministically()
    {
        var modelClient = new FakeAgentModelClient();
        modelClient.EnqueueResponse(ValidResult(confidence: 0.4));

        Tier1InvestigationOutcome outcome = await CreateAgent(modelClient)
            .InvestigateAsync(Scenario.Incident, Scenario.Evidence, "corr-1", CancellationToken.None);

        Assert.Equal(AgentDisposition.Escalate, outcome.Result.RecommendedDisposition);
        Assert.Null(outcome.Result.ProposedAction);
    }

    [Fact]
    public async Task Investigate_UnknownProposedActionType_IsStrippedAndEscalated()
    {
        var modelClient = new FakeAgentModelClient();
        var rogueAction = new RemediationAction(
            "DeleteNamespace",
            new ActionTarget("demo", "namespace", "demo"),
            new Dictionary<string, string>(),
            "inc-001-delete-1");
        modelClient.EnqueueResponse(ValidResult(proposedAction: rogueAction));

        Tier1InvestigationOutcome outcome = await CreateAgent(modelClient)
            .InvestigateAsync(Scenario.Incident, Scenario.Evidence, "corr-1", CancellationToken.None);

        Assert.Equal(AgentDisposition.Escalate, outcome.Result.RecommendedDisposition);
        Assert.Null(outcome.Result.ProposedAction);
    }

    [Fact]
    public async Task Investigate_ResolveWithoutAction_IsEscalated()
    {
        var modelClient = new FakeAgentModelClient();
        modelClient.EnqueueResponse(ValidResult() with { ProposedAction = null });

        Tier1InvestigationOutcome outcome = await CreateAgent(modelClient)
            .InvestigateAsync(Scenario.Incident, Scenario.Evidence, "corr-1", CancellationToken.None);

        Assert.Equal(AgentDisposition.Escalate, outcome.Result.RecommendedDisposition);
    }

    [Fact]
    public async Task Investigate_InvalidThenValidOutput_RepairsWithinBoundedAttempts()
    {
        var modelClient = new FakeAgentModelClient();
        modelClient.EnqueueRawOutput("not json at all");
        modelClient.EnqueueResponse(ValidResult());

        Tier1InvestigationOutcome outcome = await CreateAgent(modelClient)
            .InvestigateAsync(Scenario.Incident, Scenario.Evidence, "corr-1", CancellationToken.None);

        Assert.Equal(2, modelClient.InvocationCount);
        Assert.Equal(AgentDisposition.Resolve, outcome.Result.RecommendedDisposition);
    }

    [Fact]
    public async Task Investigate_PersistentlyInvalidOutput_FailsSafely()
    {
        var modelClient = new FakeAgentModelClient();
        modelClient.EnqueueRawOutput("bad");
        modelClient.EnqueueRawOutput("still bad");

        await Assert.ThrowsAsync<ModelResponseValidationException>(() =>
            CreateAgent(modelClient).InvestigateAsync(
                Scenario.Incident, Scenario.Evidence, "corr-1", CancellationToken.None));
        Assert.Equal(2, modelClient.InvocationCount);
    }

    [Fact]
    public async Task Investigate_MismatchedIncidentId_IsRejectedAsInvalid()
    {
        var modelClient = new FakeAgentModelClient();
        modelClient.EnqueueResponse(ValidResult() with { IncidentId = "inc-other" });
        modelClient.EnqueueResponse(ValidResult() with { IncidentId = "inc-other" });

        await Assert.ThrowsAsync<ModelResponseValidationException>(() =>
            CreateAgent(modelClient).InvestigateAsync(
                Scenario.Incident, Scenario.Evidence, "corr-1", CancellationToken.None));
    }
}
