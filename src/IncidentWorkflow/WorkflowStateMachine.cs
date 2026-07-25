namespace AzureAgenticOps.IncidentWorkflow;

/// <summary>
/// The exception thrown when an invalid workflow state transition is attempted.
/// </summary>
public sealed class InvalidWorkflowTransitionException : InvalidOperationException
{
    /// <summary>Initializes a new instance describing the invalid transition.</summary>
    /// <param name="from">The current state.</param>
    /// <param name="to">The requested state.</param>
    public InvalidWorkflowTransitionException(IncidentWorkflowState from, IncidentWorkflowState to)
        : base($"Transition from '{from}' to '{to}' is not permitted.")
    {
        From = from;
        To = to;
    }

    /// <summary>Gets the state the workflow was in.</summary>
    public IncidentWorkflowState From { get; }

    /// <summary>Gets the state that was requested.</summary>
    public IncidentWorkflowState To { get; }
}

/// <summary>
/// The deterministic transition table for the incident workflow. Every state
/// change must pass through <see cref="EnsureValid"/>; agents cannot introduce
/// transitions that are not declared here.
/// </summary>
public static class WorkflowStateMachine
{
    private static readonly IReadOnlyDictionary<IncidentWorkflowState, IReadOnlyList<IncidentWorkflowState>> Transitions =
        new Dictionary<IncidentWorkflowState, IReadOnlyList<IncidentWorkflowState>>
        {
            [IncidentWorkflowState.Received] =
                [IncidentWorkflowState.Classifying, IncidentWorkflowState.Failed],
            [IncidentWorkflowState.Classifying] =
                [IncidentWorkflowState.RuleEvaluation, IncidentWorkflowState.Failed],
            [IncidentWorkflowState.RuleEvaluation] =
                [
                    IncidentWorkflowState.Tier1Investigation,
                    IncidentWorkflowState.Executing,
                    IncidentWorkflowState.Failed,
                ],
            [IncidentWorkflowState.Tier1Investigation] =
                [
                    IncidentWorkflowState.Executing,
                    IncidentWorkflowState.Tier2Investigation,
                    IncidentWorkflowState.AwaitingEvidence,
                    IncidentWorkflowState.Failed,
                ],
            [IncidentWorkflowState.AwaitingEvidence] =
                [IncidentWorkflowState.Tier1Investigation, IncidentWorkflowState.Failed],
            [IncidentWorkflowState.Tier2Investigation] =
                [
                    IncidentWorkflowState.AwaitingApproval,
                    IncidentWorkflowState.Executing,
                    IncidentWorkflowState.Failed,
                ],
            [IncidentWorkflowState.AwaitingApproval] =
                [
                    IncidentWorkflowState.Executing,
                    IncidentWorkflowState.Rejected,
                    IncidentWorkflowState.Terminated,
                ],
            [IncidentWorkflowState.Executing] =
                [
                    IncidentWorkflowState.Verifying,
                    IncidentWorkflowState.RollingBack,
                    IncidentWorkflowState.Tier1Investigation,
                    IncidentWorkflowState.Failed,
                ],
            [IncidentWorkflowState.Verifying] =
                [
                    IncidentWorkflowState.Resolved,
                    IncidentWorkflowState.Tier1Investigation,
                    IncidentWorkflowState.Tier2Investigation,
                    IncidentWorkflowState.RollingBack,
                    IncidentWorkflowState.Failed,
                ],
            [IncidentWorkflowState.RollingBack] =
                [IncidentWorkflowState.Failed, IncidentWorkflowState.Terminated],
            [IncidentWorkflowState.Resolved] = [],
            [IncidentWorkflowState.Rejected] = [],
            [IncidentWorkflowState.Failed] = [],
            [IncidentWorkflowState.Terminated] = [],
        };

    /// <summary>Determines whether a transition is permitted.</summary>
    /// <param name="from">The current state.</param>
    /// <param name="to">The requested state.</param>
    /// <returns><see langword="true"/> when the transition is declared valid.</returns>
    public static bool IsValid(IncidentWorkflowState from, IncidentWorkflowState to) =>
        Transitions.TryGetValue(from, out IReadOnlyList<IncidentWorkflowState>? targets) &&
        targets.Contains(to);

    /// <summary>Determines whether a state is terminal.</summary>
    /// <param name="state">The state to inspect.</param>
    /// <returns><see langword="true"/> when the state has no outgoing transitions.</returns>
    public static bool IsTerminal(IncidentWorkflowState state) =>
        Transitions.TryGetValue(state, out IReadOnlyList<IncidentWorkflowState>? targets) &&
        targets.Count == 0;

    /// <summary>Throws when a transition is not permitted.</summary>
    /// <param name="from">The current state.</param>
    /// <param name="to">The requested state.</param>
    /// <exception cref="InvalidWorkflowTransitionException">The transition is not declared valid.</exception>
    public static void EnsureValid(IncidentWorkflowState from, IncidentWorkflowState to)
    {
        if (!IsValid(from, to))
        {
            throw new InvalidWorkflowTransitionException(from, to);
        }
    }
}
