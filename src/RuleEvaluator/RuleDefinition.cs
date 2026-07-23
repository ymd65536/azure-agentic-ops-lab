using AzureAgenticOps.Contracts;

namespace AzureAgenticOps.RuleEvaluator;

/// <summary>
/// A criterion that must be satisfied by at least one evidence item for a rule to match.
/// Matching is deterministic: the evidence type must match exactly and the content
/// must contain the required text (ordinal, case-insensitive).
/// </summary>
/// <param name="EvidenceType">The required evidence type, for example "log" or "config".</param>
/// <param name="ContentContains">The text that the evidence content must contain.</param>
public sealed record EvidenceMatchCriterion(
    string EvidenceType,
    string ContentContains);

/// <summary>
/// A declarative definition of a known incident pattern. Rules are data, not code,
/// so they can be reviewed, versioned, and tested independently.
/// </summary>
/// <param name="PatternName">The unique name of the known pattern.</param>
/// <param name="Description">A description of the pattern.</param>
/// <param name="Criteria">The criteria that must all be satisfied for the rule to match.</param>
/// <param name="Confidence">The confidence assigned when the rule matches, from 0.0 to 1.0.</param>
/// <param name="RecommendedDisposition">The recommended disposition when the rule matches.</param>
/// <param name="EscalateToTier2">Whether the incident must still be escalated to Tier 2.</param>
/// <param name="ProposedActionType">The predefined action type to propose, when a deterministic remediation exists.</param>
/// <param name="MaxActionAttempts">The maximum number of times the proposed action may be attempted.</param>
public sealed record RuleDefinition(
    string PatternName,
    string Description,
    IReadOnlyList<EvidenceMatchCriterion> Criteria,
    double Confidence,
    AgentDisposition RecommendedDisposition,
    bool EscalateToTier2,
    string? ProposedActionType,
    int MaxActionAttempts = 1);

/// <summary>
/// The deterministic result of rule evaluation for one incident.
/// </summary>
/// <param name="Classification">The resulting classification.</param>
/// <param name="MatchedPatternName">The matched pattern name, when a rule matched.</param>
/// <param name="MatchedEvidenceIds">The identifiers of the evidence items that satisfied the criteria.</param>
/// <param name="Confidence">The confidence of the evaluation, from 0.0 to 1.0.</param>
/// <param name="RecommendedDisposition">The recommended next step.</param>
/// <param name="EscalateToTier2">Whether the incident must be escalated to Tier 2.</param>
/// <param name="ProposedActionType">The proposed predefined action type, when applicable.</param>
/// <param name="MaxActionAttempts">The maximum number of times the proposed action may be attempted.</param>
/// <param name="ReasonSummary">A short deterministic explanation of the decision.</param>
public sealed record RuleEvaluationResult(
    IncidentClassification Classification,
    string? MatchedPatternName,
    IReadOnlyList<string> MatchedEvidenceIds,
    double Confidence,
    AgentDisposition RecommendedDisposition,
    bool EscalateToTier2,
    string? ProposedActionType,
    int MaxActionAttempts,
    string ReasonSummary);
