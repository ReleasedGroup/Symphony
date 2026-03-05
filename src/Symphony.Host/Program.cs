using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Symphony.Core.Configuration;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Storage;
using Symphony.Host.Workers;

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

builder.Services.AddSymphonySqlitePersistence(builder.Configuration);

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

app.MapGet("/api/v1/runtime", (IOptions<SymphonyRuntimeOptions> runtimeOptions, IOptions<SqliteStorageOptions> storageOptions) =>
{
    var options = runtimeOptions.Value;
    return Results.Ok(new
    {
        tracker = new
        {
            options.Tracker.Kind,
            options.Tracker.Owner,
            options.Tracker.Repo,
            options.Tracker.Milestone,
            labels = options.Tracker.Labels,
            activeStates = options.Tracker.ActiveStates,
            terminalStates = options.Tracker.TerminalStates
        },
        polling = new
        {
            options.Polling.IntervalMs
        },
        agent = new
        {
            options.Agent.MaxConcurrentAgents
        },
        persistence = new
        {
            provider = "sqlite",
            isConfigured = !string.IsNullOrWhiteSpace(storageOptions.Value.ConnectionString)
        }
    });
});

app.MapGet("/", () => Results.Redirect("/api/v1/health"));

app.Run();

return;

static bool ValidateRuntimeOptions(SymphonyRuntimeOptions options)
{
    if (!string.Equals(options.Tracker.Kind, "github", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (string.IsNullOrWhiteSpace(options.Tracker.Owner) || string.IsNullOrWhiteSpace(options.Tracker.Repo))
    {
        return false;
    }

    return options.Polling.IntervalMs >= 1_000 && options.Agent.MaxConcurrentAgents > 0;
}

public partial class Program;
