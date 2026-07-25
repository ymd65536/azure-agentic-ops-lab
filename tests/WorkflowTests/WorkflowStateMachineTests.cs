using AzureAgenticOps.Contracts;
using AzureAgenticOps.IncidentWorkflow;

namespace AzureAgenticOps.WorkflowTests;

public sealed class WorkflowStateMachineTests
{
    [Theory]
    [InlineData(IncidentWorkflowState.Tier1Investigation, IncidentWorkflowState.Resolved)]
    [InlineData(IncidentWorkflowState.AwaitingApproval, IncidentWorkflowState.Tier2Investigation)]
    [InlineData(IncidentWorkflowState.Resolved, IncidentWorkflowState.Received)]
    public void InvalidTransitions_AreRejected(IncidentWorkflowState from, IncidentWorkflowState to)
    {
        // Tier1Investigation -> Resolved is intentionally invalid: resolution
        // always passes through Executing and Verifying.
        Assert.False(WorkflowStateMachine.IsValid(from, to));
        Assert.Throws<InvalidWorkflowTransitionException>(() => WorkflowStateMachine.EnsureValid(from, to));
    }

    [Theory]
    [InlineData(IncidentWorkflowState.Tier1Investigation, IncidentWorkflowState.Tier2Investigation)]
    [InlineData(IncidentWorkflowState.Tier1Investigation, IncidentWorkflowState.Executing)]
    [InlineData(IncidentWorkflowState.Tier1Investigation, IncidentWorkflowState.AwaitingEvidence)]
    [InlineData(IncidentWorkflowState.Tier2Investigation, IncidentWorkflowState.AwaitingApproval)]
    [InlineData(IncidentWorkflowState.AwaitingApproval, IncidentWorkflowState.Executing)]
    [InlineData(IncidentWorkflowState.AwaitingApproval, IncidentWorkflowState.Rejected)]
    [InlineData(IncidentWorkflowState.AwaitingApproval, IncidentWorkflowState.Terminated)]
    [InlineData(IncidentWorkflowState.Executing, IncidentWorkflowState.Verifying)]
    [InlineData(IncidentWorkflowState.Executing, IncidentWorkflowState.RollingBack)]
    [InlineData(IncidentWorkflowState.Verifying, IncidentWorkflowState.Resolved)]
    [InlineData(IncidentWorkflowState.Verifying, IncidentWorkflowState.Tier2Investigation)]
    [InlineData(IncidentWorkflowState.Verifying, IncidentWorkflowState.RollingBack)]
    public void DeclaredTransitions_AreValid(IncidentWorkflowState from, IncidentWorkflowState to) =>
        Assert.True(WorkflowStateMachine.IsValid(from, to));

    [Theory]
    [InlineData(IncidentWorkflowState.Resolved)]
    [InlineData(IncidentWorkflowState.Rejected)]
    [InlineData(IncidentWorkflowState.Failed)]
    [InlineData(IncidentWorkflowState.Terminated)]
    public void TerminalStates_HaveNoOutgoingTransitions(IncidentWorkflowState state) =>
        Assert.True(WorkflowStateMachine.IsTerminal(state));
}
