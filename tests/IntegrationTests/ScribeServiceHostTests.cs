using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AzureAgenticOps.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace IntegrationTests;

/// <summary>
/// Tests running the ScribeService host in memory. Lifecycle events are posted
/// to the subscription route exactly as the Dapr sidecar would deliver them
/// (CloudEvents envelope) and as raw payloads for sidecar-free local runs; the
/// resulting timeline and post-incident record projections are asserted over HTTP.
/// </summary>
public sealed class ScribeServiceHostTests : IClassFixture<WebApplicationFactory<ScribeProgram>>
{
    private readonly WebApplicationFactory<ScribeProgram> _factory;

    public ScribeServiceHostTests(WebApplicationFactory<ScribeProgram> factory)
    {
        _factory = factory;
    }

    private static IncidentLifecycleEvent LifecycleEvent(
        string incidentId, string eventId, string eventType, DateTimeOffset occurredAt, string? outcome = null) => new(
        SchemaVersions.V1,
        eventId,
        incidentId,
        "corr-1",
        eventType,
        "IncidentWorkflow",
        occurredAt,
        Outcome: outcome,
        WorkflowInstanceId: "wf-1");

    private static Task<HttpResponseMessage> PostEventAsync(HttpClient client, string json) =>
        client.PostAsync(
            "/events/incident-lifecycle",
            new StringContent(json, Encoding.UTF8, "application/json"));

    [Fact]
    public async Task HealthEndpoints_ReportHealthy()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage health = await client.GetAsync("/healthz");
        using HttpResponseMessage ready = await client.GetAsync("/readyz");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task DaprSubscribe_AnnouncesLifecycleTopic()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/dapr/subscribe");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement subscription = document.RootElement[0];
        Assert.Equal("incident-pubsub", subscription.GetProperty("pubsubname").GetString());
        Assert.Equal("incident-lifecycle", subscription.GetProperty("topic").GetString());
        Assert.Equal("/events/incident-lifecycle", subscription.GetProperty("route").GetString());
    }

    [Fact]
    public async Task Events_RawAndCloudEventsEnvelope_BuildOrderedTimelineWithDedup()
    {
        using HttpClient client = _factory.CreateClient();
        var baseTime = new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);
        string received = ContractSerialization.Serialize(
            LifecycleEvent("inc-scribe-1", "evt-1", "WorkflowStarted", baseTime));
        string resolved = ContractSerialization.Serialize(
            LifecycleEvent("inc-scribe-1", "evt-2", "StateChanged", baseTime.AddMinutes(2), outcome: "resolved"));

        // Later event first (out-of-order delivery), wrapped as the sidecar would.
        using HttpResponseMessage first = await PostEventAsync(
            client, $$"""{"specversion":"1.0","type":"com.dapr.event.sent","data":{{resolved}}}""");
        // Earlier event raw (sidecar-free local run), delivered twice (duplicate).
        using HttpResponseMessage second = await PostEventAsync(client, received);
        using HttpResponseMessage duplicate = await PostEventAsync(client, received);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);

        using HttpResponseMessage timelineResponse = await client.GetAsync("/incidents/inc-scribe-1/timeline");
        Assert.Equal(HttpStatusCode.OK, timelineResponse.StatusCode);
        using JsonDocument timeline = JsonDocument.Parse(await timelineResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, timeline.RootElement.GetArrayLength());
        Assert.Equal("evt-1", timeline.RootElement[0].GetProperty("eventId").GetString());
        Assert.Equal("evt-2", timeline.RootElement[1].GetProperty("eventId").GetString());
    }

    [Fact]
    public async Task Record_SummarizesRecordedEvents()
    {
        using HttpClient client = _factory.CreateClient();
        var baseTime = new DateTimeOffset(2026, 7, 25, 13, 0, 0, TimeSpan.Zero);
        await PostEventAsync(client, ContractSerialization.Serialize(
            LifecycleEvent("inc-scribe-2", "evt-1", "ExecutionCompleted", baseTime, outcome: "succeeded")));
        await PostEventAsync(client, ContractSerialization.Serialize(
            LifecycleEvent("inc-scribe-2", "evt-2", "VerificationCompleted", baseTime.AddMinutes(1), outcome: "passed")));
        await PostEventAsync(client, ContractSerialization.Serialize(
            LifecycleEvent("inc-scribe-2", "evt-3", "StateChanged", baseTime.AddMinutes(2), outcome: "resolved")));

        using HttpResponseMessage response = await client.GetAsync("/incidents/inc-scribe-2/record");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument record = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("inc-scribe-2", record.RootElement.GetProperty("incidentId").GetString());
        Assert.Equal("resolved", record.RootElement.GetProperty("finalState").GetString());
        Assert.Equal(3, record.RootElement.GetProperty("eventCount").GetInt32());
        Assert.Equal(1, record.RootElement.GetProperty("executedActionCount").GetInt32());
        Assert.Equal("passed", record.RootElement.GetProperty("verificationOutcome").GetString());
    }

    [Fact]
    public async Task MalformedOrIncompleteEvents_AreDroppedWithoutRedelivery()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage malformed = await PostEventAsync(client, "not json at all");
        using HttpResponseMessage missingIds = await PostEventAsync(client, """{"schemaVersion":"1.0"}""");

        // 200 with DROP tells the sidecar not to redeliver a poison message.
        Assert.Equal(HttpStatusCode.OK, malformed.StatusCode);
        Assert.Contains("DROP", await malformed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, missingIds.StatusCode);
        Assert.Contains("DROP", await missingIds.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownIncident_TimelineAndRecord_ReturnNotFound()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage timeline = await client.GetAsync("/incidents/unknown/timeline");
        using HttpResponseMessage record = await client.GetAsync("/incidents/unknown/record");

        Assert.Equal(HttpStatusCode.NotFound, timeline.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, record.StatusCode);
    }
}
