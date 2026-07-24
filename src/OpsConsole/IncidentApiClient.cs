using System.Net;
using System.Net.Http.Json;
using AzureAgenticOps.Contracts;
using AzureAgenticOps.IncidentWorkflow;

namespace AzureAgenticOps.OpsConsole;

/// <summary>
/// A typed client for the read and command endpoints of the IncidentApi that the
/// console visualizes. The console only observes and forwards human decisions; it
/// never performs remediation itself.
/// </summary>
public sealed class IncidentApiClient
{
    private readonly HttpClient _httpClient;

    /// <summary>Initializes a new client.</summary>
    /// <param name="httpClient">The configured HTTP client.</param>
    public IncidentApiClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    /// <summary>Gets the status of every tracked incident run, newest first.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The tracked runs.</returns>
    public async Task<IReadOnlyList<IncidentRunView>> GetRunsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IncidentRunView>? runs = await _httpClient
            .GetFromJsonAsync<IReadOnlyList<IncidentRunView>>("incidents", ContractSerialization.Options, cancellationToken)
            .ConfigureAwait(false);
        return runs ?? [];
    }

    /// <summary>Gets the status of one incident run.</summary>
    /// <param name="incidentId">The incident identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The run status, or <see langword="null"/> when unknown.</returns>
    public async Task<IncidentRunView?> GetRunAsync(string incidentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incidentId);
        using HttpResponseMessage response = await _httpClient
            .GetAsync($"incidents/{Uri.EscapeDataString(incidentId)}", cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content
            .ReadFromJsonAsync<IncidentRunView>(ContractSerialization.Options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Gets the recorded lifecycle timeline of one incident run.</summary>
    /// <param name="incidentId">The incident identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The lifecycle events, in arrival order.</returns>
    public async Task<IReadOnlyList<IncidentLifecycleEvent>> GetTimelineAsync(
        string incidentId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incidentId);
        using HttpResponseMessage response = await _httpClient
            .GetAsync($"incidents/{Uri.EscapeDataString(incidentId)}/timeline", cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        IReadOnlyList<IncidentLifecycleEvent>? events = await response.Content
            .ReadFromJsonAsync<IReadOnlyList<IncidentLifecycleEvent>>(ContractSerialization.Options, cancellationToken)
            .ConfigureAwait(false);
        return events ?? [];
    }

    /// <summary>Submits an incident with its evidence to start a workflow run.</summary>
    /// <param name="incident">The incident to submit.</param>
    /// <param name="evidence">The evidence collected for the incident.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The initial run status.</returns>
    public async Task<IncidentRunView?> SubmitIncidentAsync(
        Incident incident,
        IReadOnlyList<IncidentEvidence> evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(evidence);

        using HttpResponseMessage response = await _httpClient
            .PostAsJsonAsync(
                "incidents",
                new { incident, evidence },
                ContractSerialization.Options,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content
            .ReadFromJsonAsync<IncidentRunView>(ContractSerialization.Options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Delivers a human approval decision as an external event.</summary>
    /// <param name="incidentId">The incident identifier.</param>
    /// <param name="outcome">The decision: approved or rejected.</param>
    /// <param name="approver">The identity of the approver.</param>
    /// <param name="reason">The stated reason for the decision.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Whether the decision was accepted by the workflow.</returns>
    public async Task<bool> SubmitApprovalAsync(
        string incidentId,
        ApprovalOutcome outcome,
        string approver,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(incidentId);
        using HttpResponseMessage response = await _httpClient
            .PostAsJsonAsync(
                $"incidents/{Uri.EscapeDataString(incidentId)}/approval",
                new { outcome, approver, reason },
                ContractSerialization.Options,
                cancellationToken)
            .ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Configures the value the mock verification runner reports for a target.
    /// This drives the demo-only success and failure paths of a scenario.
    /// </summary>
    /// <param name="target">The verification check target.</param>
    /// <param name="actualValue">The value the mock reports.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the override is stored.</returns>
    public async Task SetVerificationValueAsync(
        string target,
        string actualValue,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(actualValue);
        using HttpResponseMessage response = await _httpClient
            .PostAsJsonAsync(
                "demo/verification",
                new { target, actualValue },
                ContractSerialization.Options,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
