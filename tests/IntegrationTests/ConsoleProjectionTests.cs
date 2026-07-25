using System.Net;
using System.Net.Http.Json;
using System.Text;
using AzureAgenticOps.Contracts;
using AzureAgenticOps.IncidentApi;
using AzureAgenticOps.IncidentWorkflow;
using Microsoft.AspNetCore.Mvc.Testing;

namespace IntegrationTests;

/// <summary>
/// Tests the read-only projections the operations console renders: the list of
/// tracked runs and the recorded lifecycle timeline of one run.
/// </summary>
public sealed class ConsoleProjectionTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);

    private readonly WebApplicationFactory<Program> _factory;

    public ConsoleProjectionTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Timeline_ForUnknownIncident_ReturnsNotFound()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/incidents/does-not-exist/timeline");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CompletedRun_IsListedAndHasAnOrderedTimeline()
    {
        using HttpClient client = _factory.CreateClient();
        Incident incident = ScenarioFixtures.LoadIncident("001-known-routing-error") with
        {
            IncidentId = $"inc-console-{Guid.NewGuid():N}",
        };
        IReadOnlyList<IncidentEvidence> evidence =
        [
            .. ScenarioFixtures.LoadEvidence("001-known-routing-error")
                .Select(item => item with { IncidentId = incident.IncidentId }),
        ];

        // The mock verification runner must observe a healthy target after remediation.
        using HttpResponseMessage verificationSetup = await PostJsonAsync(
            client,
            "/demo/verification",
            new VerificationOverrideSubmission($"demo/deployment/{incident.AffectedServices[0]}", "healthy"));
        Assert.Equal(HttpStatusCode.NoContent, verificationSetup.StatusCode);

        using HttpResponseMessage submission = await PostJsonAsync(
            client, "/incidents", new IncidentSubmission(incident, evidence));
        Assert.Equal(HttpStatusCode.Accepted, submission.StatusCode);

        await ApproveWhenRequestedAsync(client, incident.IncidentId);

        IReadOnlyList<IncidentRunStatus> runs = ContractSerialization
            .Deserialize<IReadOnlyList<IncidentRunStatus>>(await client.GetStringAsync("/incidents"));
        IncidentRunStatus listed = Assert.Single(runs, run => run.IncidentId == incident.IncidentId);
        Assert.Equal(incident.Title, listed.Title);
        Assert.Equal(incident.Severity, listed.Severity);
        Assert.NotNull(listed.StartedAt);

        IReadOnlyList<IncidentLifecycleEvent> timeline = ContractSerialization
            .Deserialize<IReadOnlyList<IncidentLifecycleEvent>>(
                await client.GetStringAsync($"/incidents/{incident.IncidentId}/timeline"));

        Assert.NotEmpty(timeline);
        Assert.Equal("IncidentReceived", timeline[0].EventType);
        Assert.All(timeline, item => Assert.Equal(incident.IncidentId, item.IncidentId));
        Assert.Contains(timeline, item => item.EventType == "StateChanged");
        Assert.Contains(timeline, item => item.EventType == "ExecutionCompleted");
    }

    private static async Task ApproveWhenRequestedAsync(HttpClient client, string incidentId)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + CompletionTimeout;
        bool approvalSent = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            IncidentRunStatus status = ContractSerialization.Deserialize<IncidentRunStatus>(
                await client.GetStringAsync($"/incidents/{incidentId}"));
            if (status.IsCompleted)
            {
                return;
            }

            if (!approvalSent && status.CurrentState == IncidentWorkflowState.AwaitingApproval)
            {
                using HttpResponseMessage approval = await PostJsonAsync(
                    client,
                    $"/incidents/{incidentId}/approval",
                    new ApprovalSubmission(ApprovalOutcome.Approved, "console-test", "Approved by test."));
                approvalSent = approval.IsSuccessStatusCode;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException($"The workflow for incident '{incidentId}' did not complete in time.");
    }

    private static async Task<HttpResponseMessage> PostJsonAsync<T>(HttpClient client, string requestUri, T payload)
    {
        using var content = new StringContent(
            ContractSerialization.Serialize(payload), Encoding.UTF8, "application/json");
        return await client.PostAsync(requestUri, content);
    }
}
