using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Symphony.Core.Configuration;
using Symphony.Core.Metadata;
using Symphony.Host.Services;
using Symphony.Host.Setup;
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
        TextReader? standardInput = null,
        TextWriter? standardOutput = null,
        Action<WebApplicationBuilder>? configureBuilder = null,
        Action<IServiceCollection>? configureServices = null,
        Func<WebApplication, CancellationToken, Task>? runApplicationAsync = null,
        CancellationToken cancellationToken = default)
    {
        standardInput ??= Console.In;
        standardOutput ??= Console.Out;

        try
        {
            var commandLine = HostCommandLineOptions.Parse(args);
            if (commandLine.Mode is HostCommandMode.Version)
            {
                await standardOutput.WriteLineAsync($"{SymphonyProductInfo.Name} {SymphonyProductInfo.DisplayVersion}");
                return 0;
            }

            if (commandLine.Mode is HostCommandMode.Install)
            {
                return await SymphonyInstallCommand.RunAsync(
                    commandLine.InstallOptions!,
                    standardInput,
                    standardOutput,
                    standardError,
                    cancellationToken);
            }

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
            options.ServiceName = SymphonyProductInfo.Name;
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

        ConfigureStaticAssets(app);
        MapRoutes(app);
        return app;
    }

    private static void ConfigureStaticAssets(WebApplication app)
    {
        var webRootPath = ResolveWebRootPath();
        if (!Directory.Exists(webRootPath))
        {
            return;
        }

        var fileProvider = new PhysicalFileProvider(webRootPath);
        app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = fileProvider
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider
        });
    }

    private static string ResolveWebRootPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location) ?? AppContext.BaseDirectory, "wwwroot"),
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "Symphony.Host", "wwwroot")
        };

        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            candidates.Add(Path.Combine(probe.FullName, "wwwroot"));
            candidates.Add(Path.Combine(probe.FullName, "src", "Symphony.Host", "wwwroot"));
            probe = probe.Parent;
        }

        return candidates.FirstOrDefault(Directory.Exists)
            ?? candidates[0];
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
                application = new
                {
                    name = SymphonyProductInfo.Name,
                    version = SymphonyProductInfo.DisplayVersion
                },
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

        app.MapGet("/", () =>
        {
            var dashboardPath = Path.Combine(ResolveWebRootPath(), "index.html");
            return File.Exists(dashboardPath)
                ? Results.File(dashboardPath, "text/html; charset=utf-8")
                : Results.Redirect("/api/v1/health");
        });
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
            SymphonyCliException => $"Startup failed: {ex.Message}",
            WorkflowLoadException workflowEx => $"Startup failed ({workflowEx.Code}): {workflowEx.Message}",
            OptionsValidationException => $"Startup failed: {ex.Message}",
            _ => $"Startup failed: {ex.Message}"
        };
    }

    private enum HostCommandMode
    {
        Run,
        Install,
        Version
    }

    private sealed record HostCommandLineOptions(
        HostCommandMode Mode,
        string[] RemainingArgs,
        string? WorkflowPath,
        int? Port,
        InstallCommandOptions? InstallOptions)
    {
        public static HostCommandLineOptions Parse(IReadOnlyList<string> args)
        {
            if (args.Count > 0)
            {
                var first = args[0];
                if (first.Equals("install", StringComparison.OrdinalIgnoreCase))
                {
                    return new HostCommandLineOptions(
                        HostCommandMode.Install,
                        [],
                        WorkflowPath: null,
                        Port: null,
                        InstallCommandOptions.Parse(args.Skip(1).ToArray()));
                }

                if (first.Equals("--version", StringComparison.OrdinalIgnoreCase) ||
                    first.Equals("version", StringComparison.OrdinalIgnoreCase))
                {
                    return new HostCommandLineOptions(
                        HostCommandMode.Version,
                        [],
                        WorkflowPath: null,
                        Port: null,
                        InstallOptions: null);
                }
            }

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
                        throw new SymphonyCliException("The --port option requires an integer value.");
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

                throw new SymphonyCliException("Only one positional workflow path may be provided.");
            }

            return new HostCommandLineOptions(HostCommandMode.Run, remainingArgs.ToArray(), workflowPath, port, InstallOptions: null);
        }

        private static int ParsePort(string value)
        {
            if (!int.TryParse(value, out var port) || port < 0)
            {
                throw new SymphonyCliException($"The --port option must be an integer greater than or equal to 0. Received '{value}'.");
            }

            return port;
        }
    }
}
