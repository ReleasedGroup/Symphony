using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Symphony.Core.Configuration;
using Symphony.Host.Services;
using Symphony.Host.Workers;
using Symphony.Infrastructure.Agent.Codex;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Storage;
using Symphony.Infrastructure.Tracker.GitHub;
using Symphony.Infrastructure.Workspaces;
using Symphony.Infrastructure.Workflows;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host;

internal static class SymphonyHostApplication
{
    internal static async Task<int> RunCliAsync(
        string[] args,
        TextWriter standardError,
        Action<WebApplicationBuilder>? configureBuilder = null,
        Action<IServiceCollection>? configureServices = null,
        Func<WebApplication, CancellationToken, Task>? runApplicationAsync = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var commandLine = HostCommandLineOptions.Parse(args);
            var builder = CreateBuilder(commandLine, configureBuilder);
            ConfigureServices(builder.Services, builder.Configuration);
            configureServices?.Invoke(builder.Services);
            await ApplyHttpPortConfigurationAsync(builder, commandLine, cancellationToken);

            await using var app = await BuildApplicationAsync(builder, cancellationToken);
            if (runApplicationAsync is null)
            {
                await app.RunAsync(cancellationToken);
            }
            else
            {
                await runApplicationAsync(app, cancellationToken);
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            await standardError.WriteLineAsync(FormatStartupFailure(ex));
            return 1;
        }
    }

    private static WebApplicationBuilder CreateBuilder(
        HostCommandLineOptions commandLine,
        Action<WebApplicationBuilder>? configureBuilder)
    {
        var builder = WebApplication.CreateBuilder(commandLine.RemainingArgs);

        builder.Host.UseWindowsService(options =>
        {
            options.ServiceName = "Symphony";
        });

        configureBuilder?.Invoke(builder);

        if (!string.IsNullOrWhiteSpace(commandLine.WorkflowPath))
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{WorkflowLoaderOptions.SectionName}:Path"] = commandLine.WorkflowPath
            });
        }

        return builder;
    }

    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddProblemDetails();
        services.AddEndpointsApiExplorer();

        services
            .AddOptions<SymphonyRuntimeOptions>()
            .Bind(configuration.GetSection(SymphonyRuntimeOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(ValidateRuntimeOptions, "Symphony runtime options are invalid.")
            .ValidateOnStart();

        services
            .AddOptions<OrchestrationOptions>()
            .Bind(configuration.GetSection(OrchestrationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSymphonySqlitePersistence(configuration);
        services.AddSymphonyWorkflowServices(configuration);
        services.AddSymphonyGitHubTrackerClient();
        services.AddSymphonyCodexAgentRunner();
        services.AddSymphonyWorkspaceServices();
        services.AddSingleton<IIssueExecutionCoordinator, IssueExecutionCoordinator>();
        services.AddSingleton<RefreshSignalService>();
        services.AddScoped<OrchestrationTickService>();
        services.AddScoped<RuntimeStateService>();

        services
            .AddHealthChecks()
            .AddDbContextCheck<SymphonyDbContext>("sqlite");

        services.AddHostedService<OrchestratorWorker>();
    }

    private static async Task ApplyHttpPortConfigurationAsync(
        WebApplicationBuilder builder,
        HostCommandLineOptions commandLine,
        CancellationToken cancellationToken)
    {
        var port = commandLine.Port;
        if (!port.HasValue)
        {
            try
            {
                var configuredWorkflowPath = builder.Configuration[$"{WorkflowLoaderOptions.SectionName}:Path"];
                var workflowPath = WorkflowPathResolver.Resolve(configuredWorkflowPath);
                var definition = await new WorkflowLoader().LoadAsync(workflowPath, cancellationToken);
                port = definition.Runtime.Server.Port;
            }
            catch (WorkflowLoadException)
            {
                return;
            }
        }

        if (port.HasValue)
        {
            builder.WebHost.UseUrls($"http://127.0.0.1:{port.Value}");
        }
    }

    private static async Task<WebApplication> BuildApplicationAsync(
        WebApplicationBuilder builder,
        CancellationToken cancellationToken)
    {
        var app = builder.Build();

        await ValidateWorkflowDispatchPreflightAsync(app.Services, cancellationToken);
        await app.Services.ApplySymphonyMigrationsAsync();

        MapRoutes(app);
        return app;
    }

    private static void MapRoutes(WebApplication app)
    {
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
                        definition.Runtime.Tracker.IncludePullRequests,
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
                        definition.Runtime.Agent.MaxConcurrentAgents,
                        definition.Runtime.Agent.MaxTurns,
                        definition.Runtime.Agent.MaxRetryBackoffMs,
                        maxConcurrentAgentsByState = definition.Runtime.Agent.MaxConcurrentAgentsByState
                    },
                    codex = new
                    {
                        definition.Runtime.Codex.Command,
                        definition.Runtime.Codex.TurnTimeoutMs,
                        definition.Runtime.Codex.ApprovalPolicy,
                        definition.Runtime.Codex.ThreadSandbox,
                        definition.Runtime.Codex.TurnSandboxPolicy,
                        definition.Runtime.Codex.ReadTimeoutMs,
                        definition.Runtime.Codex.StallTimeoutMs
                    },
                    server = new
                    {
                        definition.Runtime.Server.Port
                    },
                    workspace = new
                    {
                        definition.Runtime.Workspace.Root,
                        definition.Runtime.Workspace.SharedClonePath,
                        definition.Runtime.Workspace.WorktreesRoot,
                        definition.Runtime.Workspace.BaseBranch,
                        definition.Runtime.Workspace.RemoteUrl
                    },
                    hooks = new
                    {
                        hasAfterCreate = !string.IsNullOrWhiteSpace(definition.Runtime.Hooks.AfterCreate),
                        hasBeforeRun = !string.IsNullOrWhiteSpace(definition.Runtime.Hooks.BeforeRun),
                        hasAfterRun = !string.IsNullOrWhiteSpace(definition.Runtime.Hooks.AfterRun),
                        hasBeforeRemove = !string.IsNullOrWhiteSpace(definition.Runtime.Hooks.BeforeRemove),
                        beforeRemoveSupported = true,
                        definition.Runtime.Hooks.TimeoutMs
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

        app.MapGet("/api/v1/state", async (
            RuntimeStateService runtimeStateService,
            CancellationToken cancellationToken) =>
        {
            var payload = await runtimeStateService.GetStateAsync(cancellationToken);
            return Results.Ok(payload);
        });

        app.MapGet("/api/v1/{issueIdentifier}", async (
            string issueIdentifier,
            RuntimeStateService runtimeStateService,
            CancellationToken cancellationToken) =>
        {
            var result = await runtimeStateService.GetIssueStateAsync(issueIdentifier, cancellationToken);
            return result.Found
                ? Results.Ok(result.Payload)
                : Results.NotFound(new
                {
                    error = new
                    {
                        code = "issue_not_found",
                        message = $"Issue '{issueIdentifier}' was not found in runtime state."
                    }
                });
        });

        app.MapPost("/api/v1/refresh", (
            RefreshSignalService refreshSignalService) =>
        {
            var request = refreshSignalService.RequestRefresh();
            return Results.Accepted(value: new
            {
                queued = request.Queued,
                coalesced = request.Coalesced,
                requested_at = request.RequestedAt,
                operations = new[] { "poll", "reconcile" }
            });
        });

        app.MapGet("/", () => Results.Redirect("/api/v1/health"));
    }

    private static bool ValidateRuntimeOptions(SymphonyRuntimeOptions options)
    {
        return options.Polling.IntervalMs >= 1_000 && options.Agent.MaxConcurrentAgents > 0;
    }

    private static async Task ValidateWorkflowDispatchPreflightAsync(
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var workflowProvider = scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionProvider>();
        var definition = await workflowProvider.GetCurrentAsync(cancellationToken);
        WorkflowDispatchPreflightValidator.ValidateAndResolveApiKey(definition);
    }

    private static string FormatStartupFailure(Exception ex)
    {
        return ex switch
        {
            HostCommandLineException => $"Startup failed: {ex.Message}",
            WorkflowLoadException workflowEx => $"Startup failed ({workflowEx.Code}): {workflowEx.Message}",
            OptionsValidationException => $"Startup failed: {ex.Message}",
            _ => $"Startup failed: {ex.Message}"
        };
    }

    private sealed record HostCommandLineOptions(
        string[] RemainingArgs,
        string? WorkflowPath,
        int? Port)
    {
        public static HostCommandLineOptions Parse(IReadOnlyList<string> args)
        {
            var remainingArgs = new List<string>(args.Count);
            string? workflowPath = null;
            int? port = null;

            for (var index = 0; index < args.Count; index++)
            {
                var current = args[index];
                if (current.Equals("--port", StringComparison.OrdinalIgnoreCase))
                {
                    if (index + 1 >= args.Count)
                    {
                        throw new HostCommandLineException("The --port option requires an integer value.");
                    }

                    port = ParsePort(args[++index]);
                    continue;
                }

                if (current.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
                {
                    port = ParsePort(current["--port=".Length..]);
                    continue;
                }

                if (current.StartsWith("-", StringComparison.Ordinal))
                {
                    remainingArgs.Add(current);

                    if (index + 1 < args.Count)
                    {
                        var next = args[index + 1];
                        if (!next.StartsWith("-", StringComparison.Ordinal))
                        {
                            remainingArgs.Add(next);
                            index++;
                        }
                    }

                    continue;
                }

                if (workflowPath is null)
                {
                    workflowPath = current;
                    continue;
                }

                throw new HostCommandLineException("Only one positional workflow path may be provided.");
            }

            return new HostCommandLineOptions(remainingArgs.ToArray(), workflowPath, port);
        }

        private static int ParsePort(string value)
        {
            if (!int.TryParse(value, out var port) || port < 0)
            {
                throw new HostCommandLineException($"The --port option must be an integer greater than or equal to 0. Received '{value}'.");
            }

            return port;
        }
    }

    private sealed class HostCommandLineException(string message) : Exception(message);
}
