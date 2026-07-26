using AzureAgenticOps.Contracts;
using AzureAgenticOps.ScribeService;

namespace AzureAgenticOps.WorkflowTests;

public sealed class ScribeServiceTests
{
    private static IncidentLifecycleEvent Event(
        string eventId,
        string eventType,
        DateTimeOffset occurredAt,
        string? outcome = null,
        string incidentId = "inc-001") => new(
        SchemaVersions.V1,
        eventId,
        incidentId,
        "corr-001",
        eventType,
        "IncidentWorkflow",
        occurredAt,
        Outcome: outcome,
        WorkflowInstanceId: "wf-001");

    [Fact]
    public void DuplicateEvents_AreRecordedOnce()
    {
        var builder = new IncidentTimelineBuilder();
        IncidentLifecycleEvent lifecycleEvent = Event("evt-1", "IncidentReceived", DateTimeOffset.UnixEpoch);

        Assert.True(builder.Record(lifecycleEvent));
        Assert.False(builder.Record(lifecycleEvent));
        Assert.Single(builder.BuildTimeline("inc-001"));
    }

    [Fact]
    public void Timeline_IsOrderedByOccurrenceTime()
    {
        var builder = new IncidentTimelineBuilder();
        builder.Record(Event("evt-2", "StateChanged", DateTimeOffset.UnixEpoch.AddSeconds(10), "Classifying"));
        builder.Record(Event("evt-1", "IncidentReceived", DateTimeOffset.UnixEpoch));
        builder.Record(Event("evt-3", "StateChanged", DateTimeOffset.UnixEpoch.AddSeconds(20), "Resolved"));

        IReadOnlyList<IncidentLifecycleEvent> timeline = builder.BuildTimeline("inc-001");

        Assert.Equal(["evt-1", "evt-2", "evt-3"], timeline.Select(item => item.EventId));
    }

    [Fact]
    public void UnknownIncident_ProducesEmptyTimeline()
    {
        var builder = new IncidentTimelineBuilder();
        Assert.Empty(builder.BuildTimeline("inc-unknown"));
    }

    [Fact]
    public void PostIncidentRecord_IsDerivedFromEvents()
    {
        var builder = new IncidentTimelineBuilder();
        builder.Record(Event("evt-1", "IncidentReceived", DateTimeOffset.UnixEpoch));
        builder.Record(Event("evt-2", "StateChanged", DateTimeOffset.UnixEpoch.AddSeconds(1), "Executing"));
        builder.Record(Event("evt-3", "ApprovalCompleted", DateTimeOffset.UnixEpoch.AddSeconds(2), "Approved"));
        builder.Record(Event("evt-4", "ExecutionCompleted", DateTimeOffset.UnixEpoch.AddSeconds(3), "Succeeded"));
        builder.Record(Event("evt-5", "VerificationCompleted", DateTimeOffset.UnixEpoch.AddSeconds(4), "Passed"));
        builder.Record(Event("evt-6", "StateChanged", DateTimeOffset.UnixEpoch.AddSeconds(5), "Resolved"));

        PostIncidentRecord record = new PostIncidentRecordGenerator()
            .Generate("inc-001", builder.BuildTimeline("inc-001"));

        Assert.Equal("inc-001", record.IncidentId);
        Assert.Equal("Resolved", record.FinalState);
        Assert.Equal(6, record.EventCount);
        Assert.Equal(1, record.ExecutedActionCount);
        Assert.Equal("Approved", record.ApprovalOutcome);
        Assert.Equal("Passed", record.VerificationOutcome);
        Assert.Equal(6, record.Timeline.Count);
    }

    [Fact]
    public async Task ScribeConsumesWorkflowEvents_EndToEnd()
    {
        // Run a full workflow, feed every published event to the Scribe twice to
        // simulate duplicate Pub/Sub delivery, and verify the record.
        var activities = new FakeWorkflowActivities();
        var publisher = new AzureAgenticOps.IncidentWorkflow.InMemoryLifecycleEventPublisher();
        var approvalGate = new FakeApprovalGate();
        var orchestrator = new AzureAgenticOps.IncidentWorkflow.IncidentWorkflowOrchestrator(
            activities, approvalGate, publisher);
        activities.EvidenceResults.Enqueue(() => [WorkflowTestData.Evidence()]);
        activities.Tier1Results.Enqueue(() =>
            WorkflowTestData.Tier1Result(AgentDisposition.Resolve, 0.95, WorkflowTestData.Action()));
        activities.Tier2Results.Enqueue(() => WorkflowTestData.Plan(requiresApproval: true));
        approvalGate.Decisions.Enqueue(new AzureAgenticOps.IncidentWorkflow.ApprovalDecision(AzureAgenticOps.IncidentWorkflow.ApprovalOutcome.Approved, "oncall", "Reviewed"));
        activities.VerificationResults.Enqueue(() => WorkflowTestData.Verification(VerificationOutcome.Passed));

        await orchestrator.RunAsync(WorkflowTestData.Incident(), "wf-001", "corr-001", CancellationToken.None);

        var builder = new IncidentTimelineBuilder();
        foreach (IncidentLifecycleEvent lifecycleEvent in publisher.Events)
        {
            builder.Record(lifecycleEvent);
            builder.Record(lifecycleEvent);
        }

        PostIncidentRecord record = new PostIncidentRecordGenerator()
            .Generate("inc-001", builder.BuildTimeline("inc-001"));

        Assert.Equal(publisher.Events.Count, record.EventCount);
        Assert.Equal("Resolved", record.FinalState);
        Assert.Equal(1, record.ExecutedActionCount);
    }
}
