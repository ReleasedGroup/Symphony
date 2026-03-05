using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Symphony.Core.Configuration;
using Symphony.Host.Services;
using Symphony.Host.Workers;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Storage;
using Symphony.Infrastructure.Tracker.GitHub;
using Symphony.Infrastructure.Workflows;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "Symphony";
});

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();

builder.Services
    .AddOptions<SymphonyRuntimeOptions>()
    .Bind(builder.Configuration.GetSection(SymphonyRuntimeOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(ValidateRuntimeOptions, "Symphony runtime options are invalid.")
    .ValidateOnStart();

builder.Services
    .AddOptions<OrchestrationOptions>()
    .Bind(builder.Configuration.GetSection(OrchestrationOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSymphonySqlitePersistence(builder.Configuration);
builder.Services.AddSymphonyWorkflowServices(builder.Configuration);
builder.Services.AddSymphonyGitHubTrackerClient();
builder.Services.AddScoped<OrchestrationTickService>();

builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<SymphonyDbContext>("sqlite");

builder.Services.AddHostedService<OrchestratorWorker>();

var app = builder.Build();

await app.Services.ApplySymphonyMigrationsAsync();

app.MapHealthChecks("/api/v1/health", new HealthCheckOptions
{
    AllowCachingResponses = false
});

app.MapGet("/api/v1/runtime", async (
    IOptions<SymphonyRuntimeOptions> runtimeOptions,
    IOptions<SqliteStorageOptions> storageOptions,
    IOptions<OrchestrationOptions> orchestrationOptions,
    IWorkflowDefinitionProvider workflowProvider,
    CancellationToken cancellationToken) =>
{
    var options = runtimeOptions.Value;

    object? workflow = null;
    string? workflowError = null;
    try
    {
        var definition = await workflowProvider.GetCurrentAsync(cancellationToken);
        workflow = new
        {
            definition.SourcePath,
            definition.LoadedAtUtc,
            tracker = new
            {
                definition.Runtime.Tracker.Kind,
                definition.Runtime.Tracker.Owner,
                definition.Runtime.Tracker.Repo,
                definition.Runtime.Tracker.Milestone,
                labels = definition.Runtime.Tracker.Labels,
                activeStates = definition.Runtime.Tracker.ActiveStates,
                terminalStates = definition.Runtime.Tracker.TerminalStates
            },
            polling = new
            {
                definition.Runtime.Polling.IntervalMs
            },
            agent = new
            {
                definition.Runtime.Agent.MaxConcurrentAgents
            }
        };
    }
    catch (Exception ex)
    {
        workflowError = ex.Message;
    }

    return Results.Ok(new
    {
        runtimeDefaults = new
        {
            polling = new { options.Polling.IntervalMs },
            agent = new { options.Agent.MaxConcurrentAgents }
        },
        orchestration = new
        {
            orchestrationOptions.Value.InstanceId,
            orchestrationOptions.Value.LeaseName,
            orchestrationOptions.Value.LeaseTtlSeconds
        },
        persistence = new
        {
            provider = "sqlite",
            isConfigured = !string.IsNullOrWhiteSpace(storageOptions.Value.ConnectionString)
        },
        workflow,
        workflowError
    });
});

app.MapGet("/", () => Results.Redirect("/api/v1/health"));

app.Run();

return;

static bool ValidateRuntimeOptions(SymphonyRuntimeOptions options)
{
    return options.Polling.IntervalMs >= 1_000 && options.Agent.MaxConcurrentAgents > 0;
}

public partial class Program;
