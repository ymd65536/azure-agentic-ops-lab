using AzureAgenticOps.Contracts;

namespace AzureAgenticOps.AgentRuntime;

/// <summary>
/// A decorator implementing Shadow mode. Every invocation first calls the
/// deterministic primary client and returns its result to the caller unchanged,
/// so the workflow, approval decisions, and ExecutionService never see remote
/// model output. The same input is then sent to the shadow (remote) client with
/// its own bounded timeout; the two structured results are compared and an
/// evaluation record is persisted. Shadow failures, timeouts, and invalid
/// output are recorded but never interrupt the deterministic workflow. The
/// initial implementation awaits shadow completion so completed records cannot
/// be lost on process exit.
/// </summary>
public sealed class ShadowAgentModelClient : IAgentModelClient
{
    private readonly IAgentModelClient _primary;
    private readonly IAgentModelClient _shadow;
    private readonly IEvaluationRecordWriter _recordWriter;
    private readonly TimeSpan _shadowTimeout;
    private readonly string? _scenarioName;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new shadow client.</summary>
    /// <param name="primary">The deterministic client whose result is adopted.</param>
    /// <param name="shadow">The remote client evaluated in the shadow path.</param>
    /// <param name="recordWriter">The evaluation record writer.</param>
    /// <param name="shadowTimeout">The bounded timeout for the shadow invocation.</param>
    /// <param name="scenarioName">The scenario name recorded on evaluation records, when known.</param>
    /// <param name="timeProvider">The time provider. Defaults to <see cref="TimeProvider.System"/>.</param>
    public ShadowAgentModelClient(
        IAgentModelClient primary,
        IAgentModelClient shadow,
        IEvaluationRecordWriter recordWriter,
        TimeSpan shadowTimeout,
        string? scenarioName = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(shadow);
        ArgumentNullException.ThrowIfNull(recordWriter);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(shadowTimeout, TimeSpan.Zero);
        _primary = primary;
        _shadow = shadow;
        _recordWriter = recordWriter;
        _shadowTimeout = shadowTimeout;
        _scenarioName = scenarioName;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<AgentModelResponse<T>> GenerateStructuredResponseAsync<T>(
        AgentModelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        AgentModelResponse<T> deterministicResponse =
            await _primary.GenerateStructuredResponseAsync<T>(request, cancellationToken).ConfigureAwait(false);

        await RunShadowAsync(request, deterministicResponse, cancellationToken).ConfigureAwait(false);

        return deterministicResponse;
    }

    private async Task RunShadowAsync<T>(
        AgentModelRequest request,
        AgentModelResponse<T> deterministicResponse,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();
        long startTimestamp = _timeProvider.GetTimestamp();

        AgentModelResponse<T>? shadowResponse = null;
        string? errorCategory = null;
        bool validationSucceeded = false;

        try
        {
            using var timeoutSource = new CancellationTokenSource(_shadowTimeout, _timeProvider);
            using CancellationTokenSource linkedSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
            shadowResponse = await _shadow
                .GenerateStructuredResponseAsync<T>(request, linkedSource.Token)
                .ConfigureAwait(false);
            validationSucceeded = shadowResponse.Metadata.ValidationSucceeded;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            errorCategory = "timeout";
        }
        catch (OperationCanceledException)
        {
            errorCategory = "cancelled";
        }
        catch (ModelResponseValidationException)
        {
            errorCategory = "invalid_output";
        }
        catch (TimeoutException)
        {
            errorCategory = "timeout";
        }
        catch (Exception)
        {
            // Never let a shadow failure of any kind reach the deterministic
            // workflow. Failure details stay out of the record so credentials
            // or endpoints embedded in exception messages are never persisted.
            errorCategory = "shadow_failure";
        }

        AgentEvaluationRecord record = BuildRecord(
            request,
            deterministicResponse,
            shadowResponse,
            startedAt,
            _timeProvider.GetElapsedTime(startTimestamp),
            validationSucceeded,
            errorCategory);

        try
        {
            // Record writing must also never break the workflow; the write is
            // awaited so completed records survive process shutdown.
            await _recordWriter.WriteAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Intentionally swallowed: evaluation persistence is best-effort.
        }
    }

    private AgentEvaluationRecord BuildRecord<T>(
        AgentModelRequest request,
        AgentModelResponse<T> deterministicResponse,
        AgentModelResponse<T>? shadowResponse,
        DateTimeOffset startedAt,
        TimeSpan duration,
        bool validationSucceeded,
        string? errorCategory)
    {
        EvaluationComparison? comparison = null;
        string? incidentId = null;
        IncidentClassification? classification = null;
        AgentDisposition? disposition = null;
        RiskLevel? riskLevel = null;
        IReadOnlyList<string> proposedActionTypes = [];

        if (deterministicResponse.Value is InvestigationResult deterministicInvestigation)
        {
            incidentId = deterministicInvestigation.IncidentId;
            if (shadowResponse is not null && shadowResponse.Value is InvestigationResult shadowInvestigation)
            {
                comparison = StructuredResultComparer.CompareInvestigationResults(
                    deterministicInvestigation, shadowInvestigation);
                classification = shadowInvestigation.Classification;
                disposition = shadowInvestigation.RecommendedDisposition;
                proposedActionTypes = shadowInvestigation.ProposedAction is null
                    ? []
                    : [shadowInvestigation.ProposedAction.ActionType];
            }
        }
        else if (deterministicResponse.Value is RemediationPlan deterministicPlan)
        {
            incidentId = deterministicPlan.IncidentId;
            if (shadowResponse is not null && shadowResponse.Value is RemediationPlan shadowPlan)
            {
                comparison = StructuredResultComparer.CompareRemediationPlans(deterministicPlan, shadowPlan);
                riskLevel = shadowPlan.RiskLevel;
                proposedActionTypes = shadowPlan.Actions.Select(action => action.ActionType).ToList();
            }
        }

        return new AgentEvaluationRecord(
            SchemaVersions.V1,
            incidentId,
            AgentRoleFromPrompt(request.PromptName),
            AgentExecutionMode.Shadow,
            _scenarioName,
            request.PromptName,
            request.PromptVersion,
            shadowResponse?.Metadata.ModelId,
            startedAt,
            duration.TotalMilliseconds,
            shadowResponse?.Metadata.Usage?.InputTokens,
            shadowResponse?.Metadata.Usage?.OutputTokens,
            ToolCallCount: 0,
            KnowledgeRetrievalCount: 0,
            validationSucceeded,
            RepairAttemptCount: shadowResponse?.Metadata.RetryCount ?? 0,
            classification,
            disposition,
            riskLevel,
            proposedActionTypes,
            errorCategory,
            comparison);
    }

    private static string AgentRoleFromPrompt(string promptName)
    {
        if (promptName.StartsWith("tier1", StringComparison.OrdinalIgnoreCase))
        {
            return "tier1";
        }

        return promptName.StartsWith("tier2", StringComparison.OrdinalIgnoreCase) ? "tier2" : promptName;
    }
}
