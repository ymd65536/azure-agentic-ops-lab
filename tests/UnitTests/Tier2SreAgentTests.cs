using AzureAgenticOps.AgentRuntime;
using AzureAgenticOps.Contracts;
using AzureAgenticOps.Safety;
using AzureAgenticOps.Tier2SreAgent;

namespace UnitTests;

/// <summary>Tests for the Tier 2 agent's validation and risk-floor guards.</summary>
public sealed class Tier2SreAgentTests
{
    private static readonly ScenarioLoader.Scenario Scenario = ScenarioLoader.Load("002-ambiguous-404-increase");

    private static readonly InvestigationResult Tier1Handoff = new(
        SchemaVersions.V1,
        Scenario.Incident.IncidentId,
        IncidentClassification.Ambiguous,
        "Multiple candidate causes for the 404 increase.",
        ["404 rate rose gradually with no deployment", "CDN cache hit ratio dropped"],
        [
            new AgentHypothesis("Stale client links", 0.3, ["ev-002-log"]),
            new AgentHypothesis("Incomplete content migration", 0.4, ["ev-002-deploy"]),
        ],
        0.4,
        AgentDisposition.Escalate,
        null,
        [],
        "Ambiguous evidence requires deeper analysis.");

    private static Tier2SreAgent CreateAgent(FakeAgentModelClient modelClient, Tier2AgentOptions? options = null)
    {
        var promptStore = new FilePromptStore(Path.Combine(ScenarioLoader.RepositoryRoot, "prompts"));
        return new Tier2SreAgent(modelClient, promptStore, options);
    }

    private static RemediationPlan ValidPlan(
        RiskLevel riskLevel = RiskLevel.Medium,
        bool requiresApproval = true,
        string actionType = ActionTypeCatalog.RollbackDemoDeployment)
    {
        return new RemediationPlan(
            SchemaVersions.V1,
            Scenario.Incident.IncidentId,
            "Roll back the content-service migration output on the demo deployment.",
            new AgentHypothesis("Incomplete content migration removed archive paths", 0.7, ["ev-002-deploy", "ev-002-metric"]),
            riskLevel,
            requiresApproval,
            [
                new RemediationAction(
                    actionType,
                    new ActionTarget("demo", "deployment", "content-service"),
                    new Dictionary<string, string>(),
                    "inc-002-rollback-1"),
            ],
            [new VerificationStep("HttpStatus", "http://sample-web/articles/2024-archive", "200")],
            [],
            "The migration job warnings and dropped cache hit ratio point to missing migrated content.");
    }

    [Fact]
    public async Task Plan_ValidMediumRiskPlan_RequiresApproval()
    {
        var modelClient = new FakeAgentModelClient();
        modelClient.EnqueueResponse(ValidPlan());

        Tier2PlanningOutcome outcome = await CreateAgent(modelClient)
            .PlanAsync(Scenario.Incident, Tier1Handoff, Scenario.Evidence, "corr-2", CancellationToken.None);

        Assert.Equal(RiskLevel.Medium, outcome.Plan.RiskLevel);
        Assert.True(outcome.Plan.RequiresApproval);
        Assert.Equal("tier2-remediation", outcome.ModelMetadata.PromptName);
    }

    [Fact]
    public async Task Plan_ModelCannotDowngradeRisk()
    {
        var modelClient = new FakeAgentModelClient();
        modelClient.EnqueueResponse(ValidPlan(riskLevel: RiskLevel.Low, requiresApproval: false));

        Tier2PlanningOutcome outcome = await CreateAgent(modelClient)
            .PlanAsync(Scenario.Incident, Tier1Handoff, Scenario.Evidence, "corr-2", CancellationToken.None);

        Assert.Equal(RiskLevel.Medium, outcome.Plan.RiskLevel);
        Assert.True(outcome.Plan.RequiresApproval);
    }

    [Fact]
    public async Task Plan_LowRiskActions_MayRunWithoutApprovalOnlyWhenConfigured()
    {
        var modelClient = new FakeAgentModelClient();
        modelClient.EnqueueResponse(ValidPlan(
            riskLevel: RiskLevel.Low,
            requiresApproval: false,
            actionType: ActionTypeCatalog.CollectDiagnostics));

        Tier2PlanningOutcome demoOutcome = await CreateAgent(modelClient)
            .PlanAsync(Scenario.Incident, Tier1Handoff, Scenario.Evidence, "corr-2", CancellationToken.None);

        Assert.Equal(RiskLevel.Low, demoOutcome.Plan.RiskLevel);
        Assert.False(demoOutcome.Plan.RequiresApproval);

        modelClient.EnqueueResponse(ValidPlan(
            riskLevel: RiskLevel.Low,
            requiresApproval: false,
            actionType: ActionTypeCatalog.CollectDiagnostics));

        Tier2PlanningOutcome strictOutcome = await CreateAgent(
                modelClient,
                new Tier2AgentOptions(AllowAutomaticLowRiskExecution: false))
            .PlanAsync(Scenario.Incident, Tier1Handoff, Scenario.Evidence, "corr-2", CancellationToken.None);

        Assert.True(strictOutcome.Plan.RequiresApproval);
    }

    [Fact]
    public async Task Plan_UnknownActionType_IsRejectedAsInvalid()
    {
        var modelClient = new FakeAgentModelClient();
        modelClient.EnqueueResponse(ValidPlan(actionType: "ExecuteShellCommand"));
        modelClient.EnqueueResponse(ValidPlan(actionType: "ExecuteShellCommand"));

        await Assert.ThrowsAsync<ModelResponseValidationException>(() =>
            CreateAgent(modelClient).PlanAsync(
                Scenario.Incident, Tier1Handoff, Scenario.Evidence, "corr-2", CancellationToken.None));
        Assert.Equal(2, modelClient.InvocationCount);
    }

    [Fact]
    public async Task Plan_InvalidThenValidOutput_RepairsWithinBoundedAttempts()
    {
        var modelClient = new FakeAgentModelClient();
        modelClient.EnqueueRawOutput("{ broken");
        modelClient.EnqueueResponse(ValidPlan());

        Tier2PlanningOutcome outcome = await CreateAgent(modelClient)
            .PlanAsync(Scenario.Incident, Tier1Handoff, Scenario.Evidence, "corr-2", CancellationToken.None);

        Assert.Equal(2, modelClient.InvocationCount);
        Assert.Equal(RiskLevel.Medium, outcome.Plan.RiskLevel);
    }
}
