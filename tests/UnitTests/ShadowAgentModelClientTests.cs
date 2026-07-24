using AzureAgenticOps.AgentRuntime;
using AzureAgenticOps.Contracts;

namespace UnitTests;

/// <summary>
/// Tests for Shadow mode: the deterministic result is always adopted, shadow
/// failures never surface, and every shadow invocation produces an evaluation record.
/// </summary>
public sealed class ShadowAgentModelClientTests
{
    private static readonly AgentModelRequest Tier1Request = new(
        "tier1-investigation", "1.0", "system prompt", """{"incident":{}}""", CorrelationId: "corr-1");

    private static InvestigationResult Investigation(
        AgentDisposition disposition = AgentDisposition.Resolve,
        double confidence = 0.9) => new(
        SchemaVersions.V1,
        "inc-001",
        IncidentClassification.Known,
        "summary",
        [],
        [],
        confidence,
        disposition,
        new RemediationAction(
            "RestartDemoWorkload",
            new ActionTarget("demo", "deployment", "svc"),
            new Dictionary<string, string>(),
            "inc-001-key"),
        [],
        "reasoning");

    private static ShadowAgentModelClient CreateClient(
        FakeAgentModelClient primary,
        IAgentModelClient shadow,
        InMemoryEvaluationRecordWriter writer,
        TimeSpan? timeout = null) =>
        new(primary, shadow, writer, timeout ?? TimeSpan.FromSeconds(5), scenarioName: "scenario-001");

    [Fact]
    public async Task Generate_DeterministicResultIsAdopted_EvenWhenShadowDiffers()
    {
        var primary = new FakeAgentModelClient();
        primary.EnqueueResponse(Investigation(disposition: AgentDisposition.Resolve));
        var shadow = new FakeAgentModelClient();
        shadow.EnqueueResponse(Investigation(disposition: AgentDisposition.Escalate, confidence: 0.3));
        var writer = new InMemoryEvaluationRecordWriter();

        AgentModelResponse<InvestigationResult> response = await CreateClient(primary, shadow, writer)
            .GenerateStructuredResponseAsync<InvestigationResult>(Tier1Request, CancellationToken.None);

        Assert.Equal(AgentDisposition.Resolve, response.Value.RecommendedDisposition);
        AgentEvaluationRecord record = Assert.Single(writer.Records);
        Assert.Equal(AgentExecutionMode.Shadow, record.ExecutionMode);
        Assert.Equal("tier1", record.AgentRole);
        Assert.Equal("scenario-001", record.ScenarioName);
        Assert.Equal("inc-001", record.IncidentId);
        Assert.NotNull(record.Comparison);
        Assert.False(record.Comparison.MatchesDeterministicResult);
        Assert.Contains("recommendedDisposition", record.Comparison.MismatchedFields);
    }

    [Fact]
    public async Task Generate_MatchingShadowResult_IsRecordedAsMatch()
    {
        var primary = new FakeAgentModelClient();
        primary.EnqueueResponse(Investigation());
        var shadow = new FakeAgentModelClient();
        shadow.EnqueueResponse(Investigation(), usage: new ModelUsage(100, 40));
        var writer = new InMemoryEvaluationRecordWriter();

        await CreateClient(primary, shadow, writer)
            .GenerateStructuredResponseAsync<InvestigationResult>(Tier1Request, CancellationToken.None);

        AgentEvaluationRecord record = Assert.Single(writer.Records);
        Assert.True(record.SchemaValidationSucceeded);
        Assert.True(record.Comparison?.MatchesDeterministicResult);
        Assert.Equal(100, record.InputTokens);
        Assert.Equal(40, record.OutputTokens);
        Assert.Equal(IncidentClassification.Known, record.Classification);
        Assert.Equal(AgentDisposition.Resolve, record.Disposition);
        Assert.Equal(["RestartDemoWorkload"], record.ProposedActionTypes);
    }

    [Fact]
    public async Task Generate_ShadowFailure_DoesNotAffectDeterministicResult()
    {
        var primary = new FakeAgentModelClient();
        primary.EnqueueResponse(Investigation());
        var shadow = new FakeAgentModelClient();
        shadow.EnqueueFailure(new InvalidOperationException("endpoint unavailable"));
        var writer = new InMemoryEvaluationRecordWriter();

        AgentModelResponse<InvestigationResult> response = await CreateClient(primary, shadow, writer)
            .GenerateStructuredResponseAsync<InvestigationResult>(Tier1Request, CancellationToken.None);

        Assert.Equal("inc-001", response.Value.IncidentId);
        AgentEvaluationRecord record = Assert.Single(writer.Records);
        Assert.Equal("shadow_failure", record.ErrorCategory);
        Assert.False(record.SchemaValidationSucceeded);
        Assert.Null(record.Comparison);
    }

    [Fact]
    public async Task Generate_ShadowTimeout_DoesNotFailTheWorkflow()
    {
        var primary = new FakeAgentModelClient();
        primary.EnqueueResponse(Investigation());
        var shadow = new HangingModelClient();
        var writer = new InMemoryEvaluationRecordWriter();

        AgentModelResponse<InvestigationResult> response = await CreateClient(
                primary, shadow, writer, timeout: TimeSpan.FromMilliseconds(50))
            .GenerateStructuredResponseAsync<InvestigationResult>(Tier1Request, CancellationToken.None);

        Assert.Equal("inc-001", response.Value.IncidentId);
        AgentEvaluationRecord record = Assert.Single(writer.Records);
        Assert.Equal("timeout", record.ErrorCategory);
    }

    [Fact]
    public async Task Generate_ShadowInvalidOutput_IsRecorded()
    {
        var primary = new FakeAgentModelClient();
        primary.EnqueueResponse(Investigation());
        var shadow = new FakeAgentModelClient();
        shadow.EnqueueRawOutput("this is not contract json");
        var writer = new InMemoryEvaluationRecordWriter();

        await CreateClient(primary, shadow, writer)
            .GenerateStructuredResponseAsync<InvestigationResult>(Tier1Request, CancellationToken.None);

        AgentEvaluationRecord record = Assert.Single(writer.Records);
        Assert.Equal("invalid_output", record.ErrorCategory);
        Assert.False(record.SchemaValidationSucceeded);
    }

    [Fact]
    public async Task Generate_RecordWriterFailure_DoesNotAffectDeterministicResult()
    {
        var primary = new FakeAgentModelClient();
        primary.EnqueueResponse(Investigation());
        var shadow = new FakeAgentModelClient();
        shadow.EnqueueResponse(Investigation());

        AgentModelResponse<InvestigationResult> response = await new ShadowAgentModelClient(
                primary, shadow, new ThrowingEvaluationRecordWriter(), TimeSpan.FromSeconds(5))
            .GenerateStructuredResponseAsync<InvestigationResult>(Tier1Request, CancellationToken.None);

        Assert.Equal("inc-001", response.Value.IncidentId);
    }

    [Fact]
    public async Task Generate_PrimaryCancellation_IsPropagated()
    {
        var primary = new FakeAgentModelClient();
        primary.EnqueueResponse(Investigation());
        var shadow = new FakeAgentModelClient();
        var writer = new InMemoryEvaluationRecordWriter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateClient(primary, shadow, writer)
                .GenerateStructuredResponseAsync<InvestigationResult>(Tier1Request, cancellation.Token));
        Assert.Empty(writer.Records);
    }

    [Fact]
    public async Task Generate_EvaluationRecordJson_ContainsNoCredentialMaterial()
    {
        var primary = new FakeAgentModelClient();
        primary.EnqueueResponse(Investigation());
        var shadow = new FakeAgentModelClient();
        shadow.EnqueueFailure(new InvalidOperationException(
            "401 Unauthorized calling https://secret-endpoint.invalid with api-key sk-super-secret-value"));
        var writer = new InMemoryEvaluationRecordWriter();

        await CreateClient(primary, shadow, writer)
            .GenerateStructuredResponseAsync<InvestigationResult>(Tier1Request, CancellationToken.None);

        string json = ContractSerialization.Serialize(Assert.Single(writer.Records));
        Assert.DoesNotContain("sk-super-secret-value", json);
        Assert.DoesNotContain("secret-endpoint.invalid", json);
    }

    internal sealed class InMemoryEvaluationRecordWriter : IEvaluationRecordWriter
    {
        private readonly List<AgentEvaluationRecord> _records = [];

        public IReadOnlyList<AgentEvaluationRecord> Records => _records;

        public Task WriteAsync(AgentEvaluationRecord record, CancellationToken cancellationToken)
        {
            _records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingEvaluationRecordWriter : IEvaluationRecordWriter
    {
        public Task WriteAsync(AgentEvaluationRecord record, CancellationToken cancellationToken) =>
            throw new IOException("disk full");
    }

    private sealed class HangingModelClient : IAgentModelClient
    {
        public async Task<AgentModelResponse<T>> GenerateStructuredResponseAsync<T>(
            AgentModelRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
