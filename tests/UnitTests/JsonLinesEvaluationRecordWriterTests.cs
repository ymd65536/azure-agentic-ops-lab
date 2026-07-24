using AzureAgenticOps.AgentRuntime;
using AzureAgenticOps.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace UnitTests;

/// <summary>Tests for the JSON Lines evaluation record writer.</summary>
public sealed class JsonLinesEvaluationRecordWriterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"evaluations-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static AgentEvaluationRecord Record(string incidentId) => new(
        SchemaVersions.V1,
        incidentId,
        "tier1",
        AgentExecutionMode.Shadow,
        "scenario-001",
        "tier1-investigation",
        "1.0",
        "demo-model",
        new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero),
        123.4,
        100,
        40,
        ToolCallCount: 0,
        KnowledgeRetrievalCount: 1,
        SchemaValidationSucceeded: true,
        RepairAttemptCount: 0,
        IncidentClassification.Known,
        AgentDisposition.Resolve,
        RiskLevel: null,
        ProposedActionTypes: ["RestartDemoWorkload"],
        ErrorCategory: null,
        Comparison: new EvaluationComparison(true, ["classification"], [], 0.0));

    [Fact]
    public async Task Write_ProducesOneParseableJsonLinePerRecord()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var writer = new JsonLinesEvaluationRecordWriter(_directory, timeProvider);

        await writer.WriteAsync(Record("inc-001"), CancellationToken.None);
        await writer.WriteAsync(Record("inc-002"), CancellationToken.None);

        string path = Path.Combine(_directory, "evaluations-20260724.jsonl");
        string[] lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(2, lines.Length);

        AgentEvaluationRecord first = ContractSerialization.Deserialize<AgentEvaluationRecord>(lines[0]);
        AgentEvaluationRecord second = ContractSerialization.Deserialize<AgentEvaluationRecord>(lines[1]);
        Assert.Equal("inc-001", first.IncidentId);
        Assert.Equal("inc-002", second.IncidentId);
        Assert.Equal(AgentExecutionMode.Shadow, first.ExecutionMode);
    }

    [Fact]
    public async Task Write_ConcurrentWriters_ProduceNoInterleavedLines()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
        var writer = new JsonLinesEvaluationRecordWriter(_directory, timeProvider);

        await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(index => Task.Run(() => writer.WriteAsync(Record($"inc-{index:000}"), CancellationToken.None))));

        string path = Path.Combine(_directory, "evaluations-20260724.jsonl");
        string[] lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(50, lines.Length);
        foreach (string line in lines)
        {
            AgentEvaluationRecord parsed = ContractSerialization.Deserialize<AgentEvaluationRecord>(line);
            Assert.StartsWith("inc-", parsed.IncidentId);
        }
    }
}
