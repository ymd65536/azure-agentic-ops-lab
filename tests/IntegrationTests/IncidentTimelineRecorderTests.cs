using AzureAgenticOps.Contracts;
using AzureAgenticOps.IncidentApi;

namespace IntegrationTests;

/// <summary>
/// Tests the bounded in-memory lifecycle timeline the console renders.
/// </summary>
public sealed class IncidentTimelineRecorderTests
{
    private static IncidentLifecycleEvent CreateEvent(string incidentId, string eventType) =>
        new(
            SchemaVersions.V1,
            Guid.NewGuid().ToString("N"),
            incidentId,
            "corr-1",
            eventType,
            "IncidentWorkflow",
            DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Events_AreReturnedInArrivalOrderPerIncident()
    {
        var recorder = new IncidentTimelineRecorder();

        await recorder.PublishAsync(CreateEvent("inc-1", "IncidentReceived"), CancellationToken.None);
        await recorder.PublishAsync(CreateEvent("inc-2", "IncidentReceived"), CancellationToken.None);
        await recorder.PublishAsync(CreateEvent("inc-1", "StateChanged"), CancellationToken.None);

        Assert.Equal(
            ["IncidentReceived", "StateChanged"],
            recorder.GetEvents("inc-1").Select(item => item.EventType));
        Assert.Single(recorder.GetEvents("inc-2"));
        Assert.Empty(recorder.GetEvents("inc-3"));
    }

    [Fact]
    public async Task RetentionLimits_DropTheOldestEventsAndIncidents()
    {
        var recorder = new IncidentTimelineRecorder(new IncidentTimelineOptions
        {
            MaxEventsPerIncident = 2,
            MaxIncidents = 1,
        });

        await recorder.PublishAsync(CreateEvent("inc-1", "first"), CancellationToken.None);
        await recorder.PublishAsync(CreateEvent("inc-1", "second"), CancellationToken.None);
        await recorder.PublishAsync(CreateEvent("inc-1", "third"), CancellationToken.None);

        Assert.Equal(["second", "third"], recorder.GetEvents("inc-1").Select(item => item.EventType));

        await recorder.PublishAsync(CreateEvent("inc-2", "first"), CancellationToken.None);

        Assert.Empty(recorder.GetEvents("inc-1"));
        Assert.Single(recorder.GetEvents("inc-2"));
    }
}
