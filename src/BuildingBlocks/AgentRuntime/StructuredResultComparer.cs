using AzureAgenticOps.Contracts;

namespace AzureAgenticOps.AgentRuntime;

/// <summary>
/// Compares deterministic and shadow structured agent results field by field.
/// Only structured fields are compared; free-form prose such as
/// <c>reasoningSummary</c>, summaries, and observation text is never evaluated
/// for equality.
/// </summary>
public static class StructuredResultComparer
{
    /// <summary>
    /// Compares two Tier 1 investigation results on classification, disposition,
    /// escalation, confidence delta, proposed action type, and missing evidence.
    /// </summary>
    /// <param name="deterministic">The deterministic (adopted) result.</param>
    /// <param name="shadow">The shadow model result.</param>
    /// <returns>The structured comparison.</returns>
    public static EvaluationComparison CompareInvestigationResults(
        InvestigationResult deterministic,
        InvestigationResult shadow)
    {
        ArgumentNullException.ThrowIfNull(deterministic);
        ArgumentNullException.ThrowIfNull(shadow);

        var matched = new List<string>();
        var mismatched = new List<string>();

        Record("classification", deterministic.Classification == shadow.Classification, matched, mismatched);
        Record("recommendedDisposition", deterministic.RecommendedDisposition == shadow.RecommendedDisposition, matched, mismatched);
        Record(
            "escalationRequired",
            (deterministic.RecommendedDisposition == AgentDisposition.Escalate) ==
            (shadow.RecommendedDisposition == AgentDisposition.Escalate),
            matched,
            mismatched);
        Record(
            "proposedActionType",
            string.Equals(deterministic.ProposedAction?.ActionType, shadow.ProposedAction?.ActionType, StringComparison.Ordinal),
            matched,
            mismatched);
        Record(
            "missingEvidence",
            SetEquals(deterministic.MissingEvidence, shadow.MissingEvidence),
            matched,
            mismatched);

        return new EvaluationComparison(
            MatchesDeterministicResult: mismatched.Count == 0,
            MatchedFields: matched,
            MismatchedFields: mismatched,
            ConfidenceDelta: Math.Abs(deterministic.Confidence - shadow.Confidence));
    }

    /// <summary>
    /// Compares two Tier 2 remediation plans on risk level, approval requirement,
    /// action types, verification steps, and rollback presence.
    /// </summary>
    /// <param name="deterministic">The deterministic (adopted) plan.</param>
    /// <param name="shadow">The shadow model plan.</param>
    /// <returns>The structured comparison.</returns>
    public static EvaluationComparison CompareRemediationPlans(
        RemediationPlan deterministic,
        RemediationPlan shadow)
    {
        ArgumentNullException.ThrowIfNull(deterministic);
        ArgumentNullException.ThrowIfNull(shadow);

        var matched = new List<string>();
        var mismatched = new List<string>();

        Record("riskLevel", deterministic.RiskLevel == shadow.RiskLevel, matched, mismatched);
        Record("requiresApproval", deterministic.RequiresApproval == shadow.RequiresApproval, matched, mismatched);
        Record(
            "actionTypes",
            deterministic.Actions.Select(action => action.ActionType)
                .SequenceEqual(shadow.Actions.Select(action => action.ActionType), StringComparer.Ordinal),
            matched,
            mismatched);
        Record(
            "verificationSteps",
            deterministic.Verification.Select(step => (step.CheckType, step.Target))
                .SequenceEqual(shadow.Verification.Select(step => (step.CheckType, step.Target))),
            matched,
            mismatched);
        Record(
            "rollbackPresence",
            (deterministic.Rollback.Count > 0) == (shadow.Rollback.Count > 0),
            matched,
            mismatched);

        return new EvaluationComparison(
            MatchesDeterministicResult: mismatched.Count == 0,
            MatchedFields: matched,
            MismatchedFields: mismatched);
    }

    private static void Record(string field, bool matches, List<string> matched, List<string> mismatched) =>
        (matches ? matched : mismatched).Add(field);

    private static bool SetEquals(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.ToHashSet(StringComparer.Ordinal).SetEquals(right);
}
