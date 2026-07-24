using AzureAgenticOps.Contracts;

namespace ContractTests;

/// <summary>
/// Golden tests that pin the canonical JSON representation of public contracts.
/// If one of these tests fails, the public schema has changed and the change
/// must be reviewed as a versioned contract change, not silently accepted.
/// </summary>
public class ContractSerializationTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 1, 9, 15, 0, TimeSpan.Zero);

    [Fact]
    public void Incident_SerializesToStableJson()
    {
        var incident = new Incident(
            SchemaVersions.V1,
            "inc-001",
            "HTTP 404 spike",
            "Spike of 404 responses.",
            "synthetic-monitor",
            "sev3",
            ["sample-api"],
            FixedTime,
            new Dictionary<string, string> { ["environment"] = "demo" });

        string json = ContractSerialization.Serialize(incident);

        Assert.Equal(
            "{\"schemaVersion\":\"1.0\",\"incidentId\":\"inc-001\",\"title\":\"HTTP 404 spike\"," +
            "\"description\":\"Spike of 404 responses.\",\"source\":\"synthetic-monitor\",\"severity\":\"sev3\"," +
            "\"affectedServices\":[\"sample-api\"],\"detectedAt\":\"2026-07-01T09:15:00+00:00\"," +
            "\"metadata\":{\"environment\":\"demo\"}}",
            json);
    }

    [Fact]
    public void Incident_OmitsNullOptionalProperties()
    {
        var incident = new Incident(
            SchemaVersions.V1, "inc-001", "t", "d", "s", "sev3", [], FixedTime);

        string json = ContractSerialization.Serialize(incident);

        Assert.DoesNotContain("metadata", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Enums_SerializeToStableStringValues()
    {
        Assert.Equal("\"known\"", ContractSerialization.Serialize(IncidentClassification.Known));
        Assert.Equal("\"unknown\"", ContractSerialization.Serialize(IncidentClassification.Unknown));
        Assert.Equal("\"ambiguous\"", ContractSerialization.Serialize(IncidentClassification.Ambiguous));
        Assert.Equal("\"resolve\"", ContractSerialization.Serialize(AgentDisposition.Resolve));
        Assert.Equal("\"escalate\"", ContractSerialization.Serialize(AgentDisposition.Escalate));
        Assert.Equal("\"request_more_evidence\"", ContractSerialization.Serialize(AgentDisposition.RequestMoreEvidence));
        Assert.Equal("\"low\"", ContractSerialization.Serialize(RiskLevel.Low));
        Assert.Equal("\"medium\"", ContractSerialization.Serialize(RiskLevel.Medium));
        Assert.Equal("\"high\"", ContractSerialization.Serialize(RiskLevel.High));
        Assert.Equal("\"succeeded\"", ContractSerialization.Serialize(ExecutionOutcome.Succeeded));
        Assert.Equal("\"rejected\"", ContractSerialization.Serialize(ExecutionOutcome.Rejected));
        Assert.Equal("\"passed\"", ContractSerialization.Serialize(VerificationOutcome.Passed));
        Assert.Equal("\"inconclusive\"", ContractSerialization.Serialize(VerificationOutcome.Inconclusive));
    }

    [Fact]
    public void InvestigationResult_RoundTripsThroughJson()
    {
        var result = new InvestigationResult(
            SchemaVersions.V1,
            "inc-001",
            IncidentClassification.Known,
            "Known routing error.",
            ["404 spike after config deployment"],
            [new AgentHypothesis("Route removed by config change.", 0.9, ["ev-001-config"])],
            0.9,
            AgentDisposition.Resolve,
            new RemediationAction(
                "RollbackDemoDeployment",
                new ActionTarget("demo", "deployment", "sample-api"),
                new Dictionary<string, string> { ["revision"] = "42" },
                "inc-001:rollback:1"),
            [],
            "Config diff shows the removed route matching the failing path.");

        string json = ContractSerialization.Serialize(result);
        InvestigationResult roundTripped = ContractSerialization.Deserialize<InvestigationResult>(json);

        Assert.Equal(result, roundTripped with
        {
            Observations = result.Observations,
            Hypotheses = result.Hypotheses,
            MissingEvidence = result.MissingEvidence,
            ProposedAction = result.ProposedAction,
        });
        Assert.Equal(json, ContractSerialization.Serialize(roundTripped));
    }

    [Fact]
    public void RemediationPlan_RoundTripsThroughJson()
    {
        var plan = new RemediationPlan(
            SchemaVersions.V1,
            "inc-002",
            "Purge CDN cache after confirming migration gaps.",
            new AgentHypothesis("Incomplete content migration.", 0.6, ["ev-002-deploy"]),
            RiskLevel.Medium,
            RequiresApproval: true,
            [
                new RemediationAction(
                    "RestartDemoWorkload",
                    new ActionTarget("demo", "deployment", "sample-web"),
                    new Dictionary<string, string>(),
                    "inc-002:restart:1"),
            ],
            [new VerificationStep("HttpStatus", "http://sample-web/health", "200")],
            [],
            "Migration warnings correlate with 404 paths.");

        string json = ContractSerialization.Serialize(plan);
        RemediationPlan roundTripped = ContractSerialization.Deserialize<RemediationPlan>(json);

        Assert.Equal(json, ContractSerialization.Serialize(roundTripped));
        Assert.Equal(RiskLevel.Medium, roundTripped.RiskLevel);
        Assert.True(roundTripped.RequiresApproval);
    }

    [Fact]
    public void ExecutionResult_SerializesToStableJson()
    {
        var result = new ExecutionResult(
            SchemaVersions.V1,
            "inc-001",
            "RollbackDemoDeployment",
            "inc-001:rollback:1",
            ExecutionOutcome.Succeeded,
            "Mock rollback completed.",
            1,
            FixedTime,
            FixedTime.AddSeconds(5));

        string json = ContractSerialization.Serialize(result);

        Assert.Equal(
            "{\"schemaVersion\":\"1.0\",\"incidentId\":\"inc-001\",\"actionType\":\"RollbackDemoDeployment\"," +
            "\"idempotencyKey\":\"inc-001:rollback:1\",\"outcome\":\"succeeded\",\"message\":\"Mock rollback completed.\"," +
            "\"attemptNumber\":1,\"startedAt\":\"2026-07-01T09:15:00+00:00\",\"completedAt\":\"2026-07-01T09:15:05+00:00\"}",
            json);
    }

    [Fact]
    public void VerificationResult_RoundTripsThroughJson()
    {
        var result = new VerificationResult(
            SchemaVersions.V1,
            "inc-001",
            VerificationOutcome.Passed,
            [new VerificationCheckResult("HttpStatus", "http://sample-api/health", "200", "200", true)],
            FixedTime);

        string json = ContractSerialization.Serialize(result);
        VerificationResult roundTripped = ContractSerialization.Deserialize<VerificationResult>(json);

        Assert.Equal(json, ContractSerialization.Serialize(roundTripped));
        Assert.Equal(VerificationOutcome.Passed, roundTripped.Outcome);
    }

    [Fact]
    public void IncidentLifecycleEvent_SerializesToStableJson()
    {
        var lifecycleEvent = new IncidentLifecycleEvent(
            SchemaVersions.V1,
            "evt-1",
            "inc-001",
            "corr-1",
            "IncidentReceived",
            "IncidentApi",
            FixedTime);

        string json = ContractSerialization.Serialize(lifecycleEvent);

        Assert.Equal(
            "{\"schemaVersion\":\"1.0\",\"eventId\":\"evt-1\",\"incidentId\":\"inc-001\",\"correlationId\":\"corr-1\"," +
            "\"eventType\":\"IncidentReceived\",\"component\":\"IncidentApi\"," +
            "\"occurredAt\":\"2026-07-01T09:15:00+00:00\",\"attemptNumber\":1}",
            json);
    }

    [Fact]
    public void AgentExecutionMode_SerializesToStableStringValues()
    {
        Assert.Equal("\"deterministic\"", ContractSerialization.Serialize(AgentExecutionMode.Deterministic));
        Assert.Equal("\"remoteModel\"", ContractSerialization.Serialize(AgentExecutionMode.RemoteModel));
        Assert.Equal("\"shadow\"", ContractSerialization.Serialize(AgentExecutionMode.Shadow));
    }

    [Fact]
    public void AgentEvaluationRecord_SerializesToStableJson()
    {
        var record = new AgentEvaluationRecord(
            SchemaVersions.V1,
            "inc-001",
            "tier1",
            AgentExecutionMode.Shadow,
            "scenario-001",
            "tier1-investigation",
            "1.0",
            "demo-model",
            FixedTime,
            123.5,
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
            Comparison: new EvaluationComparison(false, ["classification"], ["recommendedDisposition"], 0.25));

        string json = ContractSerialization.Serialize(record);

        Assert.Equal(
            "{\"schemaVersion\":\"1.0\",\"incidentId\":\"inc-001\",\"agentRole\":\"tier1\"," +
            "\"executionMode\":\"shadow\",\"scenarioName\":\"scenario-001\"," +
            "\"promptName\":\"tier1-investigation\",\"promptVersion\":\"1.0\",\"modelId\":\"demo-model\"," +
            "\"startedAt\":\"2026-07-01T09:15:00+00:00\",\"durationMs\":123.5," +
            "\"inputTokens\":100,\"outputTokens\":40,\"toolCallCount\":0,\"knowledgeRetrievalCount\":1," +
            "\"schemaValidationSucceeded\":true,\"repairAttemptCount\":0," +
            "\"classification\":\"known\",\"disposition\":\"resolve\"," +
            "\"proposedActionTypes\":[\"RestartDemoWorkload\"]," +
            "\"comparison\":{\"matchesDeterministicResult\":false,\"matchedFields\":[\"classification\"]," +
            "\"mismatchedFields\":[\"recommendedDisposition\"],\"confidenceDelta\":0.25}}",
            json);
    }

    [Fact]
    public void UnknownEnumValue_FailsInsteadOfGuessing()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() =>
            ContractSerialization.Deserialize<IncidentClassification>("\"catastrophic\""));
    }

    [Fact]
    public void NullJson_FailsDeserialization()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() =>
            ContractSerialization.Deserialize<Incident>("null"));
    }
}
