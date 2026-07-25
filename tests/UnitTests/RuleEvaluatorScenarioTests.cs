using AzureAgenticOps.Contracts;
using AzureAgenticOps.RuleEvaluator;

namespace UnitTests;

public class RuleEvaluatorScenarioTests
{
    private readonly IncidentRuleEvaluator _evaluator = new(DefaultRuleCatalog.Rules);

    [Fact]
    public void Scenario001_MatchesKnownRoutingConfigurationError()
    {
        ScenarioLoader.Scenario scenario = ScenarioLoader.Load("001-known-routing-error");

        RuleEvaluationResult result = _evaluator.Evaluate(scenario.Incident, scenario.Evidence);

        Assert.Equal(scenario.ExpectedClassificationResult.Classification, result.Classification);
        Assert.Equal(scenario.ExpectedClassificationResult.MatchedPatternName, result.MatchedPatternName);
        Assert.True(result.Confidence >= scenario.ExpectedClassificationResult.MinimumConfidence);
        Assert.Equal(scenario.ExpectedClassificationResult.RecommendedDisposition, result.RecommendedDisposition);
        Assert.False(result.EscalateToTier2);
        Assert.Equal(scenario.ExpectedClassificationResult.ProposedActionType, result.ProposedActionType);
        Assert.NotEmpty(result.MatchedEvidenceIds);
    }

    [Fact]
    public void Scenario002_IsUnknownAndEscalatesToTier2()
    {
        ScenarioLoader.Scenario scenario = ScenarioLoader.Load("002-ambiguous-404-increase");

        RuleEvaluationResult result = _evaluator.Evaluate(scenario.Incident, scenario.Evidence);

        Assert.Equal(IncidentClassification.Unknown, result.Classification);
        Assert.Null(result.MatchedPatternName);
        Assert.Equal(AgentDisposition.Escalate, result.RecommendedDisposition);
        Assert.True(result.EscalateToTier2);
        Assert.Null(result.ProposedActionType);
    }

    [Fact]
    public void Scenario003_DoesNotRequestUnboundedRetries()
    {
        ScenarioLoader.Scenario scenario = ScenarioLoader.Load("003-dependency-timeout");

        RuleEvaluationResult result = _evaluator.Evaluate(scenario.Incident, scenario.Evidence);

        Assert.Equal(IncidentClassification.Known, result.Classification);
        Assert.Equal("external-dependency-timeout", result.MatchedPatternName);
        Assert.Equal(AgentDisposition.Escalate, result.RecommendedDisposition);
        Assert.True(result.EscalateToTier2);
        Assert.Null(result.ProposedActionType);
        Assert.Equal(0, result.MaxActionAttempts);
        Assert.True(result.MaxActionAttempts <= scenario.ExpectedFinalResult.MaxActionAttempts);
    }

    [Fact]
    public void UnknownIncident_IsNeverGuessedAsKnown()
    {
        ScenarioLoader.Scenario scenario = ScenarioLoader.Load("002-ambiguous-404-increase");
        var evaluatorWithNoRules = new IncidentRuleEvaluator([]);

        RuleEvaluationResult result = evaluatorWithNoRules.Evaluate(scenario.Incident, scenario.Evidence);

        Assert.Equal(IncidentClassification.Unknown, result.Classification);
        Assert.True(result.EscalateToTier2);
        Assert.Equal(0.0, result.Confidence);
    }

    [Fact]
    public void MultipleMatchingRules_ReportAmbiguousAndEscalate()
    {
        ScenarioLoader.Scenario scenario = ScenarioLoader.Load("001-known-routing-error");
        RuleDefinition duplicate = DefaultRuleCatalog.Rules[0] with { PatternName = "duplicate-pattern" };
        var evaluator = new IncidentRuleEvaluator([DefaultRuleCatalog.Rules[0], duplicate]);

        RuleEvaluationResult result = evaluator.Evaluate(scenario.Incident, scenario.Evidence);

        Assert.Equal(IncidentClassification.Ambiguous, result.Classification);
        Assert.Null(result.MatchedPatternName);
        Assert.True(result.EscalateToTier2);
        Assert.Null(result.ProposedActionType);
    }
}
