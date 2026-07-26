using System.Net;
using System.Net.Http.Json;
using System.Text;
using AzureAgenticOps.Contracts;
using AzureAgenticOps.IncidentApi;
using AzureAgenticOps.IncidentWorkflow;
using Microsoft.AspNetCore.Mvc.Testing;

namespace IntegrationTests;

/// <summary>
/// End-to-end tests running the IncidentApi host in memory: incidents are
/// submitted over HTTP, approval decisions arrive as external HTTP events, and
/// the complete workflow runs against the deterministic stub model client.
/// </summary>
public sealed class IncidentApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(30);

    private readonly WebApplicationFactory<Program> _factory;

    public IncidentApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

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
    public async Task UnknownIncident_StatusAndApproval_ReturnNotFound()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage status = await client.GetAsync("/incidents/does-not-exist");
        using HttpResponseMessage approval = await PostJsonAsync(
            client, "/incidents/does-not-exist/approval", new ApprovalSubmission(ApprovalOutcome.Approved));

        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, approval.StatusCode);
    }

    [Fact]
    public async Task InvalidSubmission_IsRejected()
    {
        using HttpClient client = _factory.CreateClient();

        Incident incident = ScenarioFixtures.LoadIncident("001-known-routing-error") with { SchemaVersion = "0.9" };
        using HttpResponseMessage response = await PostJsonAsync(
            client, "/incidents", new IncidentSubmission(incident, Evidence: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Scenario001_KnownRoutingError_ResolvesAfterApproval()
    {
        using HttpClient client = _factory.CreateClient();
        Incident incident = LoadScenarioIncident("001-known-routing-error", "int-001-resolve");
        IReadOnlyList<IncidentEvidence> evidence = LoadScenarioEvidence("001-known-routing-error", incident.IncidentId);

        // The mock verification runner must observe a healthy target after remediation.
        using HttpResponseMessage verificationSetup = await PostJsonAsync(
            client, "/demo/verification",
            new VerificationOverrideSubmission($"demo/deployment/{incident.AffectedServices[0]}", "healthy"));
        Assert.Equal(HttpStatusCode.NoContent, verificationSetup.StatusCode);

        using HttpResponseMessage accepted = await PostJsonAsync(client, "/incidents", new IncidentSubmission(incident, evidence));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        // The rollback action is medium risk, so the workflow waits for human approval.
        await WaitForStateAsync(client, incident.IncidentId, IncidentWorkflowState.AwaitingApproval);

        using HttpResponseMessage approval = await PostJsonAsync(
            client, $"/incidents/{incident.IncidentId}/approval",
            new ApprovalSubmission(ApprovalOutcome.Approved, "sre-lead", "Known pattern; rollback approved."));
        Assert.Equal(HttpStatusCode.Accepted, approval.StatusCode);

        IncidentRunStatus final = await WaitForCompletionAsync(client, incident.IncidentId);
        Assert.NotNull(final.Result);
        Assert.Equal(IncidentWorkflowState.Resolved, final.Result!.FinalState);
        Assert.Contains(IncidentWorkflowState.AwaitingApproval, final.Result.StateHistory);
        Assert.Contains(IncidentWorkflowState.Tier2Investigation, final.Result.StateHistory);
    }

    [Fact]
    public async Task Scenario001_RejectedApproval_EndsRejected()
    {
        using HttpClient client = _factory.CreateClient();
        Incident incident = LoadScenarioIncident("001-known-routing-error", "int-001-reject");
        IReadOnlyList<IncidentEvidence> evidence = LoadScenarioEvidence("001-known-routing-error", incident.IncidentId);

        using HttpResponseMessage accepted = await PostJsonAsync(client, "/incidents", new IncidentSubmission(incident, evidence));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        await WaitForStateAsync(client, incident.IncidentId, IncidentWorkflowState.AwaitingApproval);

        using HttpResponseMessage approval = await PostJsonAsync(
            client, $"/incidents/{incident.IncidentId}/approval",
            new ApprovalSubmission(ApprovalOutcome.Rejected, "sre-lead", "Not during business hours."));
        Assert.Equal(HttpStatusCode.Accepted, approval.StatusCode);

        IncidentRunStatus final = await WaitForCompletionAsync(client, incident.IncidentId);
        Assert.Equal(IncidentWorkflowState.Rejected, final.Result!.FinalState);
    }

    [Fact]
    public async Task Scenario001_ApprovalTimeout_TerminatesSafely()
    {
        using WebApplicationFactory<Program> factory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("IncidentApi:ApprovalTimeoutSeconds", "1"));
        using HttpClient client = factory.CreateClient();
        Incident incident = LoadScenarioIncident("001-known-routing-error", "int-001-timeout");
        IReadOnlyList<IncidentEvidence> evidence = LoadScenarioEvidence("001-known-routing-error", incident.IncidentId);

        using HttpResponseMessage accepted = await PostJsonAsync(client, "/incidents", new IncidentSubmission(incident, evidence));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        IncidentRunStatus final = await WaitForCompletionAsync(client, incident.IncidentId);
        Assert.Equal(IncidentWorkflowState.Terminated, final.Result!.FinalState);
    }

    [Fact]
    public async Task Scenario003_DependencyTimeout_TerminatesWithoutRestartLoop()
    {
        // Use an isolated host so verification overrides configured by other
        // tests cannot leak into this scenario.
        using WebApplicationFactory<Program> factory = _factory.WithWebHostBuilder(_ => { });
        using HttpClient client = factory.CreateClient();
        Incident incident = LoadScenarioIncident("003-dependency-timeout", "int-003-bounded");
        IReadOnlyList<IncidentEvidence> evidence = LoadScenarioEvidence("003-dependency-timeout", incident.IncidentId);

        using HttpResponseMessage accepted = await PostJsonAsync(client, "/incidents", new IncidentSubmission(incident, evidence));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        // Every Tier 2 planning round presents its commands to a human before
        // execution. No verification override is configured, so verification can
        // never pass and the bounded workflow must terminate in Failed instead of
        // looping over restarts.
        IncidentRunStatus final = await ApproveEachPlanUntilCompletionAsync(client, incident.IncidentId);
        Assert.Equal(IncidentWorkflowState.Failed, final.Result!.FinalState);
        Assert.Contains(IncidentWorkflowState.Tier2Investigation, final.Result.StateHistory);
    }

    [Fact]
    public async Task Scenario005_KnownCrashLoop_ResolvesOnTheRuleFastPath()
    {
        using HttpClient client = _factory.CreateClient();
        Incident incident = LoadScenarioIncident("005-known-crashloop-restart", "int-005-fastpath");
        IReadOnlyList<IncidentEvidence> evidence = LoadScenarioEvidence("005-known-crashloop-restart", incident.IncidentId);

        using HttpResponseMessage verificationSetup = await PostJsonAsync(
            client, "/demo/verification",
            new VerificationOverrideSubmission($"demo/deployment/{incident.AffectedServices[0]}", "healthy"));
        Assert.Equal(HttpStatusCode.NoContent, verificationSetup.StatusCode);

        using HttpResponseMessage accepted = await PostJsonAsync(client, "/incidents", new IncidentSubmission(incident, evidence));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        IncidentRunStatus final = await WaitForCompletionAsync(client, incident.IncidentId);
        Assert.Equal(IncidentWorkflowState.Resolved, final.Result!.FinalState);
        Assert.DoesNotContain(IncidentWorkflowState.Tier1Investigation, final.Result.StateHistory);
        Assert.DoesNotContain(IncidentWorkflowState.Tier2Investigation, final.Result.StateHistory);
        Assert.DoesNotContain(IncidentWorkflowState.AwaitingApproval, final.Result.StateHistory);
    }

    [Fact]
    public async Task DuplicateSubmission_IsRejectedWithConflict()
    {
        using HttpClient client = _factory.CreateClient();
        Incident incident = LoadScenarioIncident("001-known-routing-error", "int-001-duplicate");
        IReadOnlyList<IncidentEvidence> evidence = LoadScenarioEvidence("001-known-routing-error", incident.IncidentId);

        using HttpResponseMessage first = await PostJsonAsync(client, "/incidents", new IncidentSubmission(incident, evidence));
        using HttpResponseMessage second = await PostJsonAsync(client, "/incidents", new IncidentSubmission(incident, evidence));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    private static Incident LoadScenarioIncident(string scenarioName, string incidentId) =>
        ScenarioFixtures.LoadIncident(scenarioName) with { IncidentId = incidentId };

    private static IReadOnlyList<IncidentEvidence> LoadScenarioEvidence(string scenarioName, string incidentId) =>
        ScenarioFixtures.LoadEvidence(scenarioName)
            .Select(item => item with { IncidentId = incidentId })
            .ToList();

    private static Task<HttpResponseMessage> PostJsonAsync<T>(HttpClient client, string requestUri, T value)
    {
        var content = new StringContent(ContractSerialization.Serialize(value), Encoding.UTF8, "application/json");
        return client.PostAsync(requestUri, content);
    }

    private static async Task<IncidentRunStatus> GetStatusAsync(HttpClient client, string incidentId)
    {
        string json = await client.GetStringAsync($"/incidents/{incidentId}");
        return ContractSerialization.Deserialize<IncidentRunStatus>(json);
    }

    private static async Task WaitForStateAsync(HttpClient client, string incidentId, IncidentWorkflowState state)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + CompletionTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            IncidentRunStatus status = await GetStatusAsync(client, incidentId);
            if (status.CurrentState == state)
            {
                return;
            }

            Assert.False(status.IsCompleted, $"The workflow completed in state {status.CurrentState} before reaching {state}.");
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.Fail($"The workflow for incident '{incidentId}' did not reach state {state} within {CompletionTimeout}.");
    }

    private static async Task<IncidentRunStatus> ApproveEachPlanUntilCompletionAsync(HttpClient client, string incidentId)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + CompletionTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            IncidentRunStatus status = await GetStatusAsync(client, incidentId);
            if (status.IsCompleted)
            {
                return status;
            }

            if (status.CurrentState == IncidentWorkflowState.AwaitingApproval)
            {
                using HttpResponseMessage approval = await PostJsonAsync(
                    client, $"/incidents/{incidentId}/approval",
                    new ApprovalSubmission(ApprovalOutcome.Approved, "sre-lead", "Bounded remediation attempt approved."));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException($"The workflow for incident '{incidentId}' did not complete within {CompletionTimeout}.");
    }

    private static async Task<IncidentRunStatus> WaitForCompletionAsync(HttpClient client, string incidentId)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + CompletionTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            IncidentRunStatus status = await GetStatusAsync(client, incidentId);
            if (status.IsCompleted)
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        throw new TimeoutException($"The workflow for incident '{incidentId}' did not complete within {CompletionTimeout}.");
    }
}
