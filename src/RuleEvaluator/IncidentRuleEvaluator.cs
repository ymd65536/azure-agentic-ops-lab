using AzureAgenticOps.Contracts;

namespace AzureAgenticOps.RuleEvaluator;

/// <summary>
/// Deterministically evaluates an incident and its evidence against a set of
/// known-pattern rules. No language model is used. When no rule matches, or more
/// than one rule matches, the evaluator never guesses: it reports the incident as
/// unknown or ambiguous and recommends escalation.
/// </summary>
public sealed class IncidentRuleEvaluator
{
    private readonly IReadOnlyList<RuleDefinition> _rules;

    /// <summary>Initializes a new evaluator with the supplied rule definitions.</summary>
    /// <param name="rules">The rule definitions to evaluate against.</param>
    public IncidentRuleEvaluator(IReadOnlyList<RuleDefinition> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _rules = rules;
    }

    /// <summary>
    /// Evaluates the incident evidence against all configured rules.
    /// </summary>
    /// <param name="incident">The incident under evaluation.</param>
    /// <param name="evidence">The evidence collected for the incident.</param>
    /// <returns>The deterministic evaluation result.</returns>
    public RuleEvaluationResult Evaluate(Incident incident, IReadOnlyList<IncidentEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(evidence);

        var matches = new List<(RuleDefinition Rule, IReadOnlyList<string> EvidenceIds)>();

        foreach (RuleDefinition rule in _rules)
        {
            if (TryMatch(rule, evidence, out IReadOnlyList<string> matchedEvidenceIds))
            {
                matches.Add((rule, matchedEvidenceIds));
            }
        }

        if (matches.Count == 1)
        {
            (RuleDefinition rule, IReadOnlyList<string> evidenceIds) = matches[0];
            return new RuleEvaluationResult(
                IncidentClassification.Known,
                rule.PatternName,
                evidenceIds,
                rule.Confidence,
                rule.RecommendedDisposition,
                rule.EscalateToTier2,
                rule.ProposedActionType,
                rule.MaxActionAttempts,
                $"Matched known pattern '{rule.PatternName}': {rule.Description}");
        }

        if (matches.Count > 1)
        {
            string names = string.Join(", ", matches.Select(match => match.Rule.PatternName));
            return new RuleEvaluationResult(
                IncidentClassification.Ambiguous,
                MatchedPatternName: null,
                matches.SelectMany(match => match.EvidenceIds).Distinct(StringComparer.Ordinal).ToArray(),
                Confidence: 0.0,
                AgentDisposition.Escalate,
                EscalateToTier2: true,
                ProposedActionType: null,
                MaxActionAttempts: 0,
                $"Multiple known patterns matched ({names}); escalating instead of guessing.");
        }

        return new RuleEvaluationResult(
            IncidentClassification.Unknown,
            MatchedPatternName: null,
            MatchedEvidenceIds: [],
            Confidence: 0.0,
            AgentDisposition.Escalate,
            EscalateToTier2: true,
            ProposedActionType: null,
            MaxActionAttempts: 0,
            "No known pattern matched; escalating instead of guessing.");
    }

    private static bool TryMatch(
        RuleDefinition rule,
        IReadOnlyList<IncidentEvidence> evidence,
        out IReadOnlyList<string> matchedEvidenceIds)
    {
        var evidenceIds = new List<string>();

        foreach (EvidenceMatchCriterion criterion in rule.Criteria)
        {
            IncidentEvidence? satisfied = evidence.FirstOrDefault(item =>
                string.Equals(item.EvidenceType, criterion.EvidenceType, StringComparison.Ordinal) &&
                item.Content.Contains(criterion.ContentContains, StringComparison.OrdinalIgnoreCase));

            if (satisfied is null)
            {
                matchedEvidenceIds = [];
                return false;
            }

            if (!evidenceIds.Contains(satisfied.EvidenceId, StringComparer.Ordinal))
            {
                evidenceIds.Add(satisfied.EvidenceId);
            }
        }

        matchedEvidenceIds = evidenceIds;
        return true;
    }
}
