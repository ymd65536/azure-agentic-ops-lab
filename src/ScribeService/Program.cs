using System.Text.Json;
using AzureAgenticOps.Contracts;
using AzureAgenticOps.ScribeService;
using Microsoft.Extensions.Options;

/// <summary>The ScribeService host entry point, exposed for integration tests.</summary>
public sealed class ScribeProgram
{
    private ScribeProgram()
    {
    }

    /// <summary>Builds and runs the Scribe host.</summary>
    /// <param name="args">The command line arguments.</param>
    public static void Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        builder.Services.AddOptions<ScribeOptions>()
            .Bind(builder.Configuration.GetSection(ScribeOptions.SectionName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.PubSubName) &&
                           !string.IsNullOrWhiteSpace(options.TopicName) &&
                           options.SubscriptionRoute.StartsWith('/'),
                "Scribe requires PubSubName, TopicName, and a rooted SubscriptionRoute.")
            .ValidateOnStart();

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.SerializerOptions.DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        });
        builder.Services.AddSingleton<IncidentTimelineBuilder>();
        builder.Services.AddSingleton(provider => new PostIncidentRecordGenerator(
            provider.GetRequiredService<TimeProvider>()));

        WebApplication app = builder.Build();

        app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
        app.MapGet("/readyz", () => Results.Ok(new { status = "ready" }));

        // Programmatic Dapr subscription: the sidecar calls this endpoint at
        // startup to discover which topics this app consumes and which route
        // delivers them.
        app.MapGet("/dapr/subscribe", (IOptions<ScribeOptions> options) => Results.Ok(new[]
        {
            new
            {
                pubsubname = options.Value.PubSubName,
                topic = options.Value.TopicName,
                route = options.Value.SubscriptionRoute,
            },
        }));

        // Lifecycle event delivery. The sidecar wraps published events in a
        // CloudEvents envelope; raw events are also accepted so the consumer can
        // be exercised locally without any sidecar. Scribe tolerates duplicate
        // delivery (dedup on event ID) and never fails the remediation path:
        // malformed payloads are dropped with a warning instead of being
        // redelivered forever.
        app.MapPost("/events/incident-lifecycle", async (HttpRequest request, IncidentTimelineBuilder timelineBuilder, ILogger<ScribeProgram> logger) =>
        {
            IncidentLifecycleEvent lifecycleEvent;
            try
            {
                using JsonDocument document = await JsonDocument.ParseAsync(
                    request.Body, cancellationToken: request.HttpContext.RequestAborted);
                JsonElement payload = document.RootElement;
                if (payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty("data", out JsonElement data) &&
                    data.ValueKind == JsonValueKind.Object)
                {
                    payload = data;
                }

                lifecycleEvent = ContractSerialization.Deserialize<IncidentLifecycleEvent>(payload.GetRawText());
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "A lifecycle event payload could not be parsed and was dropped.");
                return Results.Ok(new { status = "DROP" });
            }

            if (string.IsNullOrWhiteSpace(lifecycleEvent.IncidentId) || string.IsNullOrWhiteSpace(lifecycleEvent.EventId))
            {
                logger.LogWarning("A lifecycle event without incident or event identifier was dropped.");
                return Results.Ok(new { status = "DROP" });
            }

            bool isNew = timelineBuilder.Record(lifecycleEvent);
            if (!isNew)
            {
                logger.LogInformation(
                    "Duplicate lifecycle event {EventId} for incident {IncidentId} was ignored.",
                    lifecycleEvent.EventId, lifecycleEvent.IncidentId);
            }

            return Results.Ok(new { status = "SUCCESS" });
        });

        // Read-only projections: the ordered timeline and the deterministic
        // post-incident record draft assembled from recorded events.
        app.MapGet("/incidents/{incidentId}/timeline", (string incidentId, IncidentTimelineBuilder timelineBuilder) =>
        {
            IReadOnlyList<IncidentLifecycleEvent> timeline = timelineBuilder.BuildTimeline(incidentId);
            return timeline.Count == 0 ? Results.NotFound() : Results.Ok(timeline);
        });

        app.MapGet("/incidents/{incidentId}/record", (string incidentId, IncidentTimelineBuilder timelineBuilder, PostIncidentRecordGenerator generator) =>
        {
            IReadOnlyList<IncidentLifecycleEvent> timeline = timelineBuilder.BuildTimeline(incidentId);
            return timeline.Count == 0 ? Results.NotFound() : Results.Ok(generator.Generate(incidentId, timeline));
        });

        app.Run();
    }
}
