using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;
using Symphony.Host;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Tracker.GitHub;

namespace Symphony.Integration.Tests;

public sealed class ApiSmokeTests
{
    [Fact]
    public async Task HealthEndpoint_ShouldReturnSuccess()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        HttpStatusCode? statusCode = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    var response = await client.GetAsync("/api/v1/health", cancellationToken);
                    statusCode = response.StatusCode;
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Equal(HttpStatusCode.OK, statusCode);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task RuntimeEndpoint_ShouldReturnConfiguredDefaults()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        HttpStatusCode? statusCode = null;
        string? content = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    var response = await client.GetAsync("/api/v1/runtime", cancellationToken);
                    statusCode = response.StatusCode;
                    content = await response.Content.ReadAsStringAsync(cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Equal(HttpStatusCode.OK, statusCode);
            Assert.NotNull(content);
            Assert.Contains("\"intervalMs\":600000", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"maxConcurrentAgents\":5", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"maxTurns\":20", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task StateEndpoint_ShouldReturnSnapshotShape()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        string? content = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    content = await client.GetStringAsync("/api/v1/state", cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.NotNull(content);
            Assert.Contains("\"counts\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"tracked\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"activity\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"coordination\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"codex_totals\"", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task RefreshEndpoint_ShouldQueueBestEffortPoll()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        HttpStatusCode? statusCode = null;
        string? content = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    var response = await client.PostAsync("/api/v1/refresh", content: null, cancellationToken);
                    statusCode = response.StatusCode;
                    content = await response.Content.ReadAsStringAsync(cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Equal(HttpStatusCode.Accepted, statusCode);
            Assert.NotNull(content);
            Assert.Contains("\"queued\":true", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"operations\"", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task RootEndpoint_ShouldServeDashboardHtml()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        HttpStatusCode? statusCode = null;
        string? content = null;

        try
        {
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    var response = await client.GetAsync("/", cancellationToken);
                    statusCode = response.StatusCode;
                    content = await response.Content.ReadAsStringAsync(cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.Equal(HttpStatusCode.OK, statusCode);
            Assert.NotNull(content);
            Assert.Contains("Symphony Control Room", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task IssueEndpoint_ShouldReturnTrackedIssueDetails()
    {
        var workflowPath = CreateValidWorkflowPath();
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();
        string? content = null;

        try
        {
            await SeedIssueStateAsync(dbPath, "MT-649");

            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder => ConfigureTestServer(builder, dbPath),
                configureServices: services => RegisterFakeTracker(services),
                runApplicationAsync: async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    using var client = app.GetTestClient();
                    content = await client.GetStringAsync("/api/v1/MT-649", cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.True(exitCode == 0, stderr.ToString());
            Assert.NotNull(content);
            Assert.Contains("\"issue_identifier\":\"MT-649\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"workspace\"", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"recent_events\"", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    [Fact]
    public async Task HostStartup_ShouldFailFastWhenWorkflowApiKeyCannotBeResolved()
    {
        var missingApiKeyEnvVar = $"SYMPHONY_MISSING_API_KEY_{Guid.NewGuid():N}";
        var workflowPath = CreateWorkflowPath($$"""
            ---
            tracker:
              kind: github
              endpoint: https://api.github.com/graphql
              api_key: ${{missingApiKeyEnvVar}}
              owner: released
              repo: symphony
            polling:
              interval_ms: 600000
            agent:
              max_concurrent_agents: 5
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
            """);
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var stderr = new StringWriter();

        try
        {
            Environment.SetEnvironmentVariable(missingApiKeyEnvVar, null);
            var exitCode = await SymphonyHostApplication.RunCliAsync(
                [workflowPath],
                stderr,
                configureBuilder: builder =>
                {
                    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Persistence:ConnectionString"] = $"Data Source={dbPath};Cache=Shared;Mode=ReadWriteCreate"
                    });
                },
                runApplicationAsync: static async (app, cancellationToken) =>
                {
                    await app.StartAsync(cancellationToken);
                    await app.StopAsync(cancellationToken);
                });

            Assert.Equal(1, exitCode);
            Assert.Contains("missing_tracker_api_key", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("tracker.api_key", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TryDeleteFile(dbPath);
            TryDeleteFile(workflowPath);
        }
    }

    private static string CreateValidWorkflowPath()
    {
        return CreateWorkflowPath("""
            ---
            tracker:
              kind: github
              endpoint: https://api.github.com/graphql
              api_key: test-token
              owner: released
              repo: symphony
            polling:
              interval_ms: 600000
            agent:
              max_concurrent_agents: 5
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
            """);
    }

    private static string CreateWorkflowPath(string content)
    {
        var workflowPath = Path.Combine(Path.GetTempPath(), $"symphony-int-workflow-{Guid.NewGuid():N}.md");
        File.WriteAllText(workflowPath, content);
        return workflowPath;
    }

    private static void ConfigureTestServer(WebApplicationBuilder builder, string dbPath)
    {
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:ConnectionString"] = $"Data Source={dbPath};Cache=Shared;Mode=ReadWriteCreate"
        });
    }

    private static void RegisterFakeTracker(IServiceCollection services)
    {
        var trackerClient = new FakeGitHubTrackerClient();
        services.AddSingleton<ITrackerClient>(trackerClient);
        services.AddSingleton<IGitHubTrackerClient>(trackerClient);
    }

    private static async Task SeedIssueStateAsync(string dbPath, string issueIdentifier)
    {
        var options = new DbContextOptionsBuilder<SymphonyDbContext>()
            .UseSqlite($"Data Source={dbPath};Cache=Shared;Mode=ReadWriteCreate")
            .Options;

        await using var dbContext = new SymphonyDbContext(options);
        await dbContext.Database.MigrateAsync();

        dbContext.Runs.Add(new RunEntity
        {
            Id = "run-1",
            IssueId = "issue-1",
            IssueIdentifier = issueIdentifier,
            OwnerInstanceId = "instance-1",
            Status = RunStatusNames.Running,
            State = "Open",
            SessionId = "thread-1-turn-1",
            LastEvent = "notification",
            LastMessage = "Working on tests",
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            LastEventAtUtc = DateTimeOffset.UtcNow,
            TurnCount = 2,
            InputTokens = 10,
            OutputTokens = 5,
            TotalTokens = 15
        });
        dbContext.RunAttempts.Add(new RunAttemptEntity
        {
            Id = "attempt-1",
            RunId = "run-1",
            IssueId = "issue-1",
            Status = RunStatusNames.Running,
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        dbContext.WorkspaceRecords.Add(new WorkspaceRecordEntity
        {
            IssueId = "issue-1",
            IssueIdentifier = issueIdentifier,
            WorkspacePath = @"C:\tmp\MT-649",
            BranchName = "feature/mt-649",
            LastPreparedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2)
        });
        dbContext.EventLog.Add(new EventLogEntity
        {
            IssueId = "issue-1",
            IssueIdentifier = issueIdentifier,
            RunId = "run-1",
            RunAttemptId = "attempt-1",
            SessionId = "thread-1-turn-1",
            EventName = "notification",
            Level = "Information",
            Message = "Working on tests",
            OccurredAtUtc = DateTimeOffset.UtcNow
        });
        dbContext.IssueCache.Add(new IssueCacheEntity
        {
            IssueId = "issue-1",
            Identifier = issueIdentifier,
            Title = "Add runtime dashboard",
            State = "Open",
            Url = "https://github.com/released/symphony/issues/649",
            Milestone = "Sprint 12",
            LabelsJson = "[\"dashboard\",\"ui\"]",
            PullRequestsJson = "[]",
            BlockedByJson = "[]",
            CachedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        dbContext.InstanceLeases.Add(new InstanceLeaseEntity
        {
            LeaseName = "poll-dispatch",
            OwnerInstanceId = "instance-1",
            AcquiredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10)
        });

        await dbContext.SaveChangesAsync();
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

    private static void TryDeleteFile(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
        }
    }
}
