using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;
using Symphony.Host;
using Symphony.Infrastructure.Tracker.GitHub;
using Symphony.Infrastructure.Workflows;

namespace Symphony.Integration.Tests;

[Collection(CurrentDirectoryCollection.Name)]
public sealed class HostLifecycleTests
{
    [Fact]
    public async Task RunCliAsync_ShouldUseDefaultWorkflowPathFromCurrentDirectory()
    {
        var tempDirectory = CreateTempDirectory();
        var workflowPath = Path.Combine(tempDirectory, "WORKFLOW.md");
        var dbPath = Path.Combine(tempDirectory, "default-workflow.db");
        var stderr = new StringWriter();
        var trackerClient = new FakeGitHubTrackerClient();
        string[] urls = [];
        string? loadedWorkflowPath = null;

        await File.WriteAllTextAsync(workflowPath, CreateWorkflowContent(serverPort: 0));

        try
        {
            using var currentDirectory = new CurrentDirectoryScope(tempDirectory);
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [],
                stderr,
                configureBuilder: builder => AddTestConfiguration(builder, dbPath),
                configureServices: services =>
                {
                    services.AddSingleton<ITrackerClient>(trackerClient);
                    services.AddSingleton<IGitHubTrackerClient>(trackerClient);
                },
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    urls = [.. app.Urls];
                    loadedWorkflowPath = await GetLoadedWorkflowPathAsync(app, cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Equal(Path.GetFullPath(workflowPath), loadedWorkflowPath);
            Assert.Single(urls);
            Assert.StartsWith("http://127.0.0.1:", urls[0], StringComparison.OrdinalIgnoreCase);
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(workflowPath);
            File.Delete(dbPath);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunCliAsync_ShouldUseExplicitWorkflowPathAndPreferCliPortOverWorkflowPort()
    {
        var tempDirectory = CreateTempDirectory();
        var workflowPath = Path.Combine(tempDirectory, "custom-workflow.md");
        var dbPath = Path.Combine(tempDirectory, "explicit-workflow.db");
        var stderr = new StringWriter();
        var trackerClient = new FakeGitHubTrackerClient();
        string[] urls = [];
        string? loadedWorkflowPath = null;

        await File.WriteAllTextAsync(workflowPath, CreateWorkflowContent(serverPort: 61123));

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                ["--port", "0", workflowPath],
                stderr,
                configureBuilder: builder => AddTestConfiguration(builder, dbPath),
                configureServices: services =>
                {
                    services.AddSingleton<ITrackerClient>(trackerClient);
                    services.AddSingleton<IGitHubTrackerClient>(trackerClient);
                },
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    urls = [.. app.Urls];
                    loadedWorkflowPath = await GetLoadedWorkflowPathAsync(app, cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Equal(Path.GetFullPath(workflowPath), loadedWorkflowPath);
            Assert.Single(urls);
            Assert.StartsWith("http://127.0.0.1:", urls[0], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(":61123", urls[0], StringComparison.OrdinalIgnoreCase);
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(workflowPath);
            File.Delete(dbPath);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunCliAsync_ShouldPreserveUnknownOptionValuePairsBeforeWorkflowPath()
    {
        var tempDirectory = CreateTempDirectory();
        var workflowPath = Path.Combine(tempDirectory, "environment-workflow.md");
        var dbPath = Path.Combine(tempDirectory, "environment-workflow.db");
        var stderr = new StringWriter();
        var trackerClient = new FakeGitHubTrackerClient();
        string? loadedWorkflowPath = null;

        await File.WriteAllTextAsync(workflowPath, CreateWorkflowContent(serverPort: 0));

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                ["--environment", "Development", workflowPath],
                stderr,
                configureBuilder: builder => AddTestConfiguration(builder, dbPath),
                configureServices: services =>
                {
                    services.AddSingleton<ITrackerClient>(trackerClient);
                    services.AddSingleton<IGitHubTrackerClient>(trackerClient);
                },
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    loadedWorkflowPath = await GetLoadedWorkflowPathAsync(app, cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Equal(Path.GetFullPath(workflowPath), loadedWorkflowPath);
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(workflowPath);
            File.Delete(dbPath);
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunCliAsync_ShouldReturnNonZeroWhenExplicitWorkflowPathIsMissing()
    {
        var tempDirectory = CreateTempDirectory();
        var missingWorkflowPath = Path.Combine(tempDirectory, "missing-workflow.md");
        var stderr = new StringWriter();

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync([missingWorkflowPath], stderr);

            Assert.Equal(1, exitCode);
            Assert.Contains("missing_workflow_file", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Path.GetFullPath(missingWorkflowPath), stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunCliAsync_ShouldReturnNonZeroWhenDefaultWorkflowPathIsMissing()
    {
        var tempDirectory = CreateTempDirectory();
        var stderr = new StringWriter();

        try
        {
            using var currentDirectory = new CurrentDirectoryScope(tempDirectory);
            var exitCode = await SymphonyHostApplication.RunCliAsync([], stderr);

            Assert.Equal(1, exitCode);
            Assert.Contains("missing_workflow_file", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("WORKFLOW.md", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunCliAsync_ShouldCreateDefaultSqliteDirectoryWhenMissing()
    {
        var tempDirectory = CreateTempDirectory();
        var workflowPath = Path.Combine(tempDirectory, "WORKFLOW.md");
        var databaseDirectory = Path.Combine(tempDirectory, "data");
        var databasePath = Path.Combine(databaseDirectory, "symphony.db");
        var stderr = new StringWriter();
        var trackerClient = new FakeGitHubTrackerClient();

        await File.WriteAllTextAsync(workflowPath, CreateWorkflowContent(serverPort: 0));

        try
        {
            using var currentDirectory = new CurrentDirectoryScope(tempDirectory);
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [],
                stderr,
                configureServices: services =>
                {
                    services.AddSingleton<ITrackerClient>(trackerClient);
                    services.AddSingleton<IGitHubTrackerClient>(trackerClient);
                },
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.True(Directory.Exists(databaseDirectory));
            Assert.True(File.Exists(databasePath));
            Assert.Equal(string.Empty, stderr.ToString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static void AddTestConfiguration(WebApplicationBuilder builder, string dbPath)
    {
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:ConnectionString"] = $"Data Source={dbPath};Cache=Shared;Mode=ReadWriteCreate"
        });
    }

    private static async Task<string> GetLoadedWorkflowPathAsync(WebApplication app, CancellationToken cancellationToken)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionProvider>();
        var definition = await provider.GetCurrentAsync(cancellationToken);
        return definition.SourcePath;
    }

    private static string CreateWorkflowContent(int serverPort)
    {
        return $$"""
            ---
            tracker:
              kind: github
              endpoint: https://api.github.com/graphql
              api_key: test-token
              owner: released
              repo: symphony
              active_states: []
              terminal_states: []
            polling:
              interval_ms: 600000
            agent:
              max_concurrent_agents: 5
            server:
              port: {{serverPort}}
            codex:
              command: codex app-server
              turn_timeout_ms: 3600000
              approval_policy: never
              thread_sandbox: danger-full-access
              turn_sandbox_policy: danger-full-access
              read_timeout_ms: 5000
              stall_timeout_ms: 300000
            workspace:
              root: ./workspaces
              shared_clone_path: ./workspaces/repo
              worktrees_root: ./workspaces/worktrees
              base_branch: main
            hooks:
              timeout_ms: 60000
            ---
            Prompt body.
            """;
    }

    private static string CreateTempDirectory()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"symphony-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        return tempDirectory;
    }

    private sealed class FakeGitHubTrackerClient : IGitHubTrackerClient
    {
        public Task<IReadOnlyList<NormalizedIssue>> FetchCandidateIssuesAsync(
            TrackerQuery query,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<NormalizedIssue>>([]);
        }

        public Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByStatesAsync(
            TrackerQuery query,
            IReadOnlyList<string> states,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<NormalizedIssue>>([]);
        }

        public Task<IReadOnlyList<IssueStateSnapshot>> FetchIssueStatesByIdsAsync(
            TrackerQuery query,
            IReadOnlyList<string> issueIds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<IssueStateSnapshot>>([]);
        }

        public Task<GitHubGraphQlExecutionResult> ExecuteGitHubGraphQlAsync(
            TrackerQuery query,
            string graphQlDocument,
            string? variablesJson,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitHubGraphQlExecutionResult(true, "{\"data\":{}}"));
        }
    }

    private sealed class CurrentDirectoryScope : IDisposable
    {
        private readonly string _originalPath = Directory.GetCurrentDirectory();

        public CurrentDirectoryScope(string path)
        {
            Directory.SetCurrentDirectory(path);
        }

        public void Dispose()
        {
            Directory.SetCurrentDirectory(_originalPath);
        }
    }
}

[CollectionDefinition(CurrentDirectoryCollection.Name, DisableParallelization = true)]
public sealed class CurrentDirectoryCollection
{
    public const string Name = "CurrentDirectory";
}
