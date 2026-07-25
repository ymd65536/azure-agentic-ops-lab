using System.Text.Json;
using System.Text.Json.Serialization;
using AzureAgenticOps.AgentRuntime;
using AzureAgenticOps.Contracts;
using AzureAgenticOps.ExecutionService;
using AzureAgenticOps.IncidentApi;
using AzureAgenticOps.IncidentWorkflow;
using AzureAgenticOps.RuleEvaluator;
using AzureAgenticOps.Safety;
using AzureAgenticOps.Tier1SreAgent;
using AzureAgenticOps.VerificationService;
using Dapr.Workflow;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<AgentRuntimeOptions>()
    .Bind(builder.Configuration.GetSection(AgentRuntimeOptions.SectionName))
    .Validate(options =>
    {
        return options.Validate(out _);
    }, "The AgentRuntime configuration is invalid. Check Mode (Deterministic, RemoteModel, Shadow) and the RemoteModel section.")
    .ValidateOnStart();
builder.Services.AddOptions<IncidentApiOptions>()
    .Bind(builder.Configuration.GetSection(IncidentApiOptions.SectionName))
    .Validate(options => options.ApprovalTimeoutSeconds > 0, "ApprovalTimeoutSeconds must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<DaprPublisherOptions>()
    .Bind(builder.Configuration.GetSection(DaprPublisherOptions.SectionName))
    .Configure(options =>
    {
        // The Dapr sidecar injects DAPR_HTTP_PORT; when present it wins over configuration.
        if (int.TryParse(Environment.GetEnvironmentVariable("DAPR_HTTP_PORT"), out int sidecarPort))
        {
            options.HttpPort = sidecarPort;
        }
    })
    .Validate(options => options.HttpPort is > 0 and < 65536, "Dapr HttpPort must be a valid port.")
    .ValidateOnStart();

builder.Services.AddOptions<WorkflowHostingOptions>()
    .Bind(builder.Configuration.GetSection(WorkflowHostingOptions.SectionName))
    .Validate(options => options.Validate(out _), "Workflow:Engine must be 'InProcess' or 'Dapr'.")
    .ValidateOnStart();

builder.Services.AddOptions<IncidentTimelineOptions>()
    .Bind(builder.Configuration.GetSection(IncidentTimelineOptions.SectionName))
    .Validate(
        options => options.MaxEventsPerIncident > 0 && options.MaxIncidents > 0,
        "IncidentTimeline retention limits must be positive.")
    .ValidateOnStart();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMetrics();
builder.Services.AddSingleton<AzureAgenticOps.Observability.AgenticOpsMetrics>();

// OpenTelemetry SDK. Spans and metrics are always collected from the shared
// ActivitySource and Meter; the OTLP exporter is opt-in so local runs and
// tests stay free of network dependencies.
OpenTelemetryBuilder openTelemetry = builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(
        serviceName: builder.Configuration["OpenTelemetry:ServiceName"] ?? "incident-api",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString(),
        serviceInstanceId: Environment.MachineName))
    .WithTracing(tracing => tracing
        .AddSource(AzureAgenticOps.Observability.ObservabilityNames.ActivitySourceName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation())
    .WithMetrics(metrics => metrics
        .AddMeter(AzureAgenticOps.Observability.ObservabilityNames.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation());
if (!string.IsNullOrWhiteSpace(
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ??
        builder.Configuration["OpenTelemetry:OtlpEndpoint"]))
{
    string? configuredEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
    openTelemetry.UseOtlpExporter(
        OpenTelemetry.Exporter.OtlpExportProtocol.Grpc,
        new Uri(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? configuredEndpoint!));
}

// Deterministic building blocks.
builder.Services.AddSingleton(new ActionPolicyEvaluator(ActionPolicyOptions.DemoDefaults));
builder.Services.AddSingleton(new IncidentRuleEvaluator(DefaultRuleCatalog.Rules));
builder.Services.AddSingleton<InMemoryEvidenceStore>();
builder.Services.AddSingleton<MockVerificationCheckRunner>();
builder.Services.AddSingleton<IVerificationCheckRunner>(provider => provider.GetRequiredService<MockVerificationCheckRunner>());
builder.Services.AddSingleton(provider => new VerificationEvaluator(
    provider.GetRequiredService<IVerificationCheckRunner>(),
    provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(provider => new MockExecutionService(
    provider.GetRequiredService<ActionPolicyEvaluator>(),
    provider.GetRequiredService<TimeProvider>()));

// Agent runtime. The execution mode selects the model client composition:
//   Deterministic (default) — only the deterministic stub; no external communication.
//   RemoteModel — the remote model's structured output is used by the workflow.
//   Shadow — the deterministic result is adopted while the same input is sent to
//   the remote model and the structured comparison is recorded for evaluation.
// The remote transport is composed only when a remote endpoint is configured;
// otherwise the placeholder keeps failing safely and the default Deterministic
// mode performs no external communication at all.
builder.Services.AddHttpClient(nameof(FoundryChatCompletionTransport));
builder.Services.AddSingleton<IChatCompletionTransport>(provider =>
{
    AgentRuntimeOptions options = provider.GetRequiredService<IOptions<AgentRuntimeOptions>>().Value;
    RemoteModelOptions remote = options.RemoteModel;
    if (string.IsNullOrWhiteSpace(remote.Endpoint) || string.IsNullOrWhiteSpace(remote.ModelId))
    {
        return new UnconfiguredChatCompletionTransport();
    }

    IHttpClientFactory httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
    HttpClient httpClient = httpClientFactory.CreateClient(nameof(FoundryChatCompletionTransport));

    IChatCompletionAuthenticator authenticator;
    if (string.Equals(remote.AuthMode, RemoteModelOptions.ApiKeySecretReferenceAuthMode, StringComparison.OrdinalIgnoreCase))
    {
        DaprPublisherOptions daprOptions = provider.GetRequiredService<IOptions<DaprPublisherOptions>>().Value;
        authenticator = new ApiKeyChatCompletionAuthenticator(
            new DaprSecretStoreSecretResolver(httpClient, daprOptions.HttpPort),
            remote.ApiKeySecretName!);
    }
    else
    {
        authenticator = new BearerTokenChatCompletionAuthenticator(
            new AzureIdentityAccessTokenProvider(remote.TokenScope));
    }

    return new FoundryChatCompletionTransport(httpClient, remote, authenticator);
});
builder.Services.AddSingleton(provider => new DeterministicStubModelClient(
    provider.GetRequiredService<IncidentRuleEvaluator>(),
    provider.GetRequiredService<ActionPolicyEvaluator>(),
    provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IEvaluationRecordWriter>(provider =>
{
    AgentRuntimeOptions options = provider.GetRequiredService<IOptions<AgentRuntimeOptions>>().Value;
    return new JsonLinesEvaluationRecordWriter(
        options.Shadow.EvaluationOutputDirectory,
        provider.GetRequiredService<TimeProvider>());
});
builder.Services.AddSingleton<IAgentModelClient>(provider =>
{
    AgentRuntimeOptions options = provider.GetRequiredService<IOptions<AgentRuntimeOptions>>().Value;
    if (!options.TryGetMode(out AgentExecutionMode mode))
    {
        throw new InvalidOperationException($"Unknown AgentRuntime:Mode '{options.Mode}'.");
    }

    DeterministicStubModelClient deterministic = provider.GetRequiredService<DeterministicStubModelClient>();
    TimeProvider timeProvider = provider.GetRequiredService<TimeProvider>();

    return mode switch
    {
        AgentExecutionMode.Deterministic => deterministic,
        AgentExecutionMode.RemoteModel => new RemoteAgentModelClient(
            provider.GetRequiredService<IChatCompletionTransport>(),
            options.RemoteModel,
            timeProvider),
        AgentExecutionMode.Shadow => new ShadowAgentModelClient(
            deterministic,
            new RemoteAgentModelClient(
                provider.GetRequiredService<IChatCompletionTransport>(),
                options.RemoteModel,
                timeProvider),
            provider.GetRequiredService<IEvaluationRecordWriter>(),
            TimeSpan.FromSeconds(options.Shadow.TimeoutSeconds),
            options.Shadow.ScenarioName,
            timeProvider),
        _ => throw new InvalidOperationException($"Unknown AgentRuntime:Mode '{options.Mode}'."),
    };
});
builder.Services.AddSingleton<IPromptStore>(provider =>
{
    IncidentApiOptions options = provider.GetRequiredService<IOptions<IncidentApiOptions>>().Value;
    return new FilePromptStore(ResolveContentPath(provider, options.PromptsRoot));
});
builder.Services.AddSingleton(provider =>
{
    IncidentApiOptions options = provider.GetRequiredService<IOptions<IncidentApiOptions>>().Value;
    return new InsightsCapability(KnowledgeBase.LoadFromFile(ResolveContentPath(provider, options.KnowledgeBasePath)));
});
builder.Services.AddSingleton(provider => new AzureAgenticOps.Tier1SreAgent.Tier1SreAgent(
    provider.GetRequiredService<IAgentModelClient>(),
    provider.GetRequiredService<IPromptStore>(),
    provider.GetRequiredService<InsightsCapability>()));
builder.Services.AddSingleton(provider => new AzureAgenticOps.Tier2SreAgent.Tier2SreAgent(
    provider.GetRequiredService<IAgentModelClient>(),
    provider.GetRequiredService<IPromptStore>()));

// Workflow hosting.
builder.Services.AddSingleton<ExternalEventApprovalGate>();
builder.Services.AddSingleton<IApprovalGate>(provider => provider.GetRequiredService<ExternalEventApprovalGate>());
builder.Services.AddSingleton<IIncidentWorkflowActivities, InProcessWorkflowActivities>();
builder.Services.AddSingleton<WorkflowStateObserver>();
builder.Services.AddSingleton(provider =>
    provider.GetRequiredService<IOptions<IncidentTimelineOptions>>().Value);
builder.Services.AddSingleton<IncidentTimelineRecorder>();
builder.Services.AddHttpClient<DaprLifecycleEventPublisher>();
builder.Services.AddSingleton<ILifecycleEventPublisher>(provider => new CompositeLifecycleEventPublisher(
[
    provider.GetRequiredService<WorkflowStateObserver>(),
    provider.GetRequiredService<IncidentTimelineRecorder>(),
    provider.GetRequiredService<DaprLifecycleEventPublisher>(),
]));
builder.Services.AddSingleton(provider => new IncidentWorkflowOrchestrator(
    provider.GetRequiredService<IIncidentWorkflowActivities>(),
    provider.GetRequiredService<IApprovalGate>(),
    provider.GetRequiredService<ILifecycleEventPublisher>(),
    provider.GetRequiredService<IncidentWorkflowOptions>(),
    provider.GetRequiredService<TimeProvider>(),
    provider.GetRequiredService<AzureAgenticOps.Observability.AgenticOpsMetrics>()));
builder.Services.AddSingleton<IncidentRunRegistry>();

// Workflow hosting engine. The default in-process engine keeps local runs free
// of external dependencies; the Dapr engine hosts the same deterministic
// orchestrator as a durable Dapr Workflow through the sidecar.
builder.Services.AddSingleton(provider =>
{
    IncidentApiOptions options = provider.GetRequiredService<IOptions<IncidentApiOptions>>().Value;
    return IncidentWorkflowOptions.Default with
    {
        ApprovalTimeout = TimeSpan.FromSeconds(options.ApprovalTimeoutSeconds),
    };
});
var workflowHosting = new WorkflowHostingOptions();
builder.Configuration.GetSection(WorkflowHostingOptions.SectionName).Bind(workflowHosting);
if (workflowHosting.UsesDaprEngine)
{
    builder.Services.AddDaprWorkflow(options =>
    {
        options.RegisterWorkflow<DaprIncidentWorkflow>();
        options.RegisterActivity<CollectEvidenceActivity>();
        options.RegisterActivity<EvaluateRulesActivity>();
        options.RegisterActivity<Tier1InvestigationActivity>();
        options.RegisterActivity<Tier2PlanningActivity>();
        options.RegisterActivity<ExecuteActionActivity>();
        options.RegisterActivity<VerifyTier1RemediationActivity>();
        options.RegisterActivity<VerifyPlanActivity>();
        options.RegisterActivity<PublishLifecycleEventActivity>();
    });
    builder.Services.AddSingleton<IIncidentWorkflowRunner>(provider => new DaprIncidentWorkflowRunner(
        provider.GetRequiredService<Dapr.Workflow.DaprWorkflowClient>(),
        provider.GetRequiredService<IncidentWorkflowOptions>()));
}
else
{
    builder.Services.AddSingleton<IIncidentWorkflowRunner>(provider => new InProcessIncidentWorkflowRunner(
        provider.GetRequiredService<IncidentWorkflowOrchestrator>(),
        provider.GetRequiredService<ExternalEventApprovalGate>()));
}

WebApplication app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/readyz", () => Results.Ok(new { status = "ready" }));

app.MapPost("/incidents", (IncidentSubmission submission, InMemoryEvidenceStore evidenceStore, IncidentRunRegistry registry) =>
{
    if (submission?.Incident is null ||
        string.IsNullOrWhiteSpace(submission.Incident.IncidentId) ||
        submission.Incident.SchemaVersion != SchemaVersions.V1)
    {
        return Results.BadRequest(new { error = $"A valid incident with schema version '{SchemaVersions.V1}' is required." });
    }

    IReadOnlyList<IncidentEvidence> evidence = submission.Evidence ?? [];
    if (evidence.Any(item => item.IncidentId != submission.Incident.IncidentId))
    {
        return Results.BadRequest(new { error = "All evidence items must reference the submitted incident." });
    }

    evidenceStore.Store(submission.Incident.IncidentId, evidence);
    IncidentRunStatus? status = registry.TryStartRun(submission.Incident);
    return status is null
        ? Results.Conflict(new { error = $"A workflow for incident '{submission.Incident.IncidentId}' already exists." })
        : Results.AcceptedAtRoute("GetIncidentStatus", new { incidentId = submission.Incident.IncidentId }, status);
});

// Read-only projections consumed by the operations console.
app.MapGet("/incidents", (IncidentRunRegistry registry) => Results.Ok(registry.GetStatuses()));

app.MapGet("/incidents/{incidentId}/timeline", (string incidentId, IncidentRunRegistry registry, IncidentTimelineRecorder timeline) =>
    registry.GetStatus(incidentId) is null
        ? Results.NotFound()
        : Results.Ok(timeline.GetEvents(incidentId)));

app.MapGet("/incidents/{incidentId}", (string incidentId, IncidentRunRegistry registry) =>
{
    IncidentRunStatus? status = registry.GetStatus(incidentId);
    return status is null ? Results.NotFound() : Results.Ok(status);
}).WithName("GetIncidentStatus");

app.MapPost("/incidents/{incidentId}/approval", async (string incidentId, ApprovalSubmission submission, IncidentRunRegistry registry, IIncidentWorkflowRunner runner, CancellationToken cancellationToken) =>
{
    IncidentRunStatus? status = registry.GetStatus(incidentId);
    if (status is null)
    {
        return Results.NotFound();
    }

    if (submission is null || submission.Outcome == ApprovalOutcome.TimedOut)
    {
        return Results.BadRequest(new { error = "The approval outcome must be 'approved' or 'rejected'." });
    }

    bool delivered = await runner.TryDeliverApprovalAsync(
        incidentId,
        status.WorkflowInstanceId,
        new ApprovalDecision(submission.Outcome, submission.Approver, submission.Reason),
        cancellationToken);
    return delivered
        ? Results.Accepted($"/incidents/{incidentId}", new { incidentId, outcome = submission.Outcome })
        : Results.Conflict(new { error = $"An approval decision for incident '{incidentId}' was already delivered." });
});

// Demo-only: configures the value the mock verification runner reports for a
// target, so scenarios can drive verification success and failure locally.
app.MapPost("/demo/verification", (VerificationOverrideSubmission submission, MockVerificationCheckRunner checkRunner) =>
{
    if (submission is null || string.IsNullOrWhiteSpace(submission.Target) || submission.ActualValue is null)
    {
        return Results.BadRequest(new { error = "Both 'target' and 'actualValue' are required." });
    }

    checkRunner.SetActualValue(submission.Target, submission.ActualValue);
    return Results.NoContent();
});

app.Run();

static string ResolveContentPath(IServiceProvider provider, string path)
{
    if (Path.IsPathRooted(path))
    {
        return path;
    }

    IWebHostEnvironment environment = provider.GetRequiredService<IWebHostEnvironment>();
    string contentRootCandidate = Path.Combine(environment.ContentRootPath, path);
    if (File.Exists(contentRootCandidate) || Directory.Exists(contentRootCandidate))
    {
        return contentRootCandidate;
    }

    // Fall back to the build output, where the repository prompts and knowledge
    // fixtures are copied for local runs and container images.
    return Path.Combine(AppContext.BaseDirectory, path);
}

/// <summary>The entry point class, exposed for integration tests.</summary>
public sealed partial class Program
{
    private Program()
    {
    }
}
