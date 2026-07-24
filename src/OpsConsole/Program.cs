using AzureAgenticOps.OpsConsole;
using AzureAgenticOps.OpsConsole.Components;
using Microsoft.Extensions.Options;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<OpsConsoleOptions>()
    .Bind(builder.Configuration.GetSection(OpsConsoleOptions.SectionName))
    .Validate(
        options => Uri.TryCreate(options.IncidentApiBaseUrl, UriKind.Absolute, out _),
        "OpsConsole:IncidentApiBaseUrl must be an absolute URL.")
    .Validate(
        options => options.RefreshIntervalSeconds > 0,
        "OpsConsole:RefreshIntervalSeconds must be positive.")
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(provider =>
{
    OpsConsoleOptions options = provider.GetRequiredService<IOptions<OpsConsoleOptions>>().Value;
    return new ScenarioCatalog(ResolveContentPath(provider, options.ScenariosRoot));
});
builder.Services.AddHttpClient<IncidentApiClient>((provider, client) =>
{
    OpsConsoleOptions options = provider.GetRequiredService<IOptions<OpsConsoleOptions>>().Value;
    // The trailing slash keeps relative request URIs inside the API base path.
    client.BaseAddress = new Uri(options.IncidentApiBaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped(provider => new ScenarioLauncher(
    provider.GetRequiredService<ScenarioCatalog>(),
    provider.GetRequiredService<IncidentApiClient>(),
    provider.GetRequiredService<TimeProvider>()));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

WebApplication app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/readyz", () => Results.Ok(new { status = "ready" }));

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string ResolveContentPath(IServiceProvider provider, string path)
{
    if (Path.IsPathRooted(path))
    {
        return path;
    }

    IWebHostEnvironment environment = provider.GetRequiredService<IWebHostEnvironment>();
    string contentRootCandidate = Path.Combine(environment.ContentRootPath, path);
    return Directory.Exists(contentRootCandidate)
        ? contentRootCandidate
        : Path.Combine(AppContext.BaseDirectory, path);
}

/// <summary>The entry point class, exposed for integration tests.</summary>
public sealed partial class Program
{
    private Program()
    {
    }
}
