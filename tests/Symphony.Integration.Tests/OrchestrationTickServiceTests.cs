using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symphony.Core.Abstractions;
using Symphony.Core.Configuration;
using Symphony.Core.Models;
using Symphony.Host.Services;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Tracker.GitHub;
using Symphony.Infrastructure.Workflows;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Integration.Tests;

public sealed class OrchestrationTickServiceTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [Trait("Spec", "17.4")]
    public async Task Coordinator_ShouldOnlyScheduleSuccessfulContinuationWhileFiltersMatch(
        bool configureLabels, bool matchesFilters, bool expectRetry)
    {
        var root = Directory.CreateTempSubdirectory("symphony-continuation-").FullName;
        try
        {
            var workflow = BuildWorkflowDefinition(1);
            workflow = workflow with { Runtime = workflow.Runtime with
            {
                Tracker = workflow.Runtime.Tracker with { Labels = configureLabels ? ["symphony-test"] : [] },
                Workspace = workflow.Runtime.Workspace with { Root = root }
            } };
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Services.AddDbContext<SymphonyDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "test.db")};Pooling=False"));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddScoped<IOrchestrationCoordinationStore, OrchestrationCoordinationStore>();
            builder.Services.AddSingleton<ITrackerClient>(new FakeTrackerClient([], new Dictionary<string, string> { ["issue-1"] = "Open" }, matchesFilters));
            builder.Services.AddSingleton<IWorkspaceManager>(new PreparedWorkspaceManager(root));
            builder.Services.AddSingleton<IWorkspaceHookRunner, NoOpHookRunner>();
            builder.Services.AddSingleton<IWorkflowPromptRenderer, WorkflowPromptRenderer>();
            builder.Services.AddSingleton<IAgentRunner, SuccessfulAgentRunner>();
            builder.Services.AddSingleton<IssueExecutionCoordinator>();
            using var host = builder.Build();
            await using var scope = host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SymphonyDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Runs.Add(new RunEntity { Id = "run-1", IssueId = "issue-1", IssueIdentifier = "#1", State = "Open", Status = RunStatusNames.Running });
            db.RunAttempts.Add(new RunAttemptEntity { Id = "attempt-1", RunId = "run-1", IssueId = "issue-1", Status = RunStatusNames.Running });
            db.DispatchClaims.Add(new DispatchClaimEntity { IssueId = "issue-1", IssueIdentifier = "#1", ClaimedByInstanceId = "instance-1" });
            await db.SaveChangesAsync();
            var coordinator = host.Services.GetRequiredService<IssueExecutionCoordinator>();
            var request = new IssueExecutionRequest("run-1", "attempt-1", "instance-1", null, BuildIssue("issue-1", "#1", "Open", null), workflow);
            // Await the complete lifecycle, including claim release, without polling the fire-and-forget entry point.
            var execute = typeof(IssueExecutionCoordinator).GetMethod("ExecuteRunAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)execute.Invoke(coordinator, [request, new CancellationTokenSource()])!;
            db.ChangeTracker.Clear();

            Assert.Equal(expectRetry ? 1 : 0, await db.RetryQueue.CountAsync());
            Assert.Equal(RunStatusNames.Succeeded, (await db.RunAttempts.SingleAsync()).Status);
            var run = await db.Runs.SingleAsync();
            Assert.Equal("Open", run.State);
            Assert.Equal(expectRetry ? RunStatusNames.Retrying : RunStatusNames.Succeeded, run.Status);
            if (!expectRetry)
            {
                Assert.NotNull((await db.DispatchClaims.SingleAsync()).ReleasedAtUtc);
                Assert.True(await db.EventLog.AnyAsync(entry => entry.EventName == "continuation_stopped"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class PreparedWorkspaceManager(string root) : IWorkspaceManager
    {
        public Task<WorkspacePreparationResult> PrepareIssueWorkspaceAsync(WorkspacePreparationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspacePreparationResult(root, "branch", false));
        public Task<WorkspaceCleanupResult> CleanupIssueWorkspaceAsync(WorkspaceCleanupRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("An open issue's workspace must be retained.");
    }

    private sealed class NoOpHookRunner : IWorkspaceHookRunner
    {
        public Task RunHookAsync(WorkspaceHookRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SuccessfulAgentRunner : IAgentRunner
    {
        public Task<AgentRunResult> RunIssueAsync(AgentRunRequest request, Func<AgentRunUpdate, CancellationToken, Task>? onUpdate = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentRunResult(true, 0, "", "", TimeSpan.Zero));
    }

    [Fact]
    [Trait("Spec", "17.4")]
    public async Task RunTickAsync_ShouldStopOpenRunOutsideExecutionFiltersWithoutCleanup()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], new Dictionary<string, string> { ["issue-1"] = "Open" }, false),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning, stopReturnsFalse: true));
        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        Assert.Equal("Open", run.State);
        Assert.Equal(RunStatusNames.CanceledByReconciliation, run.Status);
        Assert.Empty(harness.WorkspaceManager.CleanupRequests);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Empty(harness.Coordinator.StartRequests);
        Assert.Equal(RunStatusNames.CanceledByReconciliation, (await harness.DbContext.DispatchClaims.SingleAsync()).Status);
    }

    [Fact]
    [Trait("Spec", "17.4")]
    public async Task RunTickAsync_ShouldReleasePersistedRetryOutsideExecutionFilters()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], new Dictionary<string, string> { ["issue-1"] = "Open" }, false),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1");
        var retry = await harness.DbContext.RetryQueue.SingleAsync();
        retry.DueAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        await harness.DbContext.SaveChangesAsync();
        harness.DbContext.ChangeTracker.Clear();

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Empty(harness.Coordinator.StartRequests);
        Assert.Empty(harness.WorkspaceManager.CleanupRequests);
        Assert.Equal("Open", (await harness.DbContext.Runs.SingleAsync()).State);
    }

    [Fact]
    public async Task RunTickAsync_ShouldScheduleContinuationRetryAfterSuccessfulDispatch()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-1", "#1", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.Service.RunTickAsync(CancellationToken.None);

        var retryEntry = await harness.DbContext.RetryQueue.SingleAsync();
        Assert.Equal("issue-1", retryEntry.IssueId);
        Assert.Equal(1, retryEntry.Attempt);
        Assert.Equal(RetryDelayTypes.Continuation, retryEntry.DelayType);
        Assert.Equal(RunStatusNames.Retrying, (await harness.DbContext.Runs.SingleAsync()).Status);
    }

    [Fact]
    public async Task RunTickAsync_ShouldUseBackoffRetryAfterFailure()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-1", "#1", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Failure));

        await harness.Service.RunTickAsync(CancellationToken.None);

        var retryEntry = await harness.DbContext.RetryQueue.SingleAsync();
        Assert.Equal(1, retryEntry.Attempt);
        Assert.Equal(RetryDelayTypes.Backoff, retryEntry.DelayType);
        Assert.True(retryEntry.DueAtUtc > DateTimeOffset.UtcNow.AddSeconds(9));
    }

    [Fact]
    public async Task RunTickAsync_ShouldHonorPerStateConcurrencyLimits()
    {
        var workflow = BuildWorkflowDefinition(
            maxConcurrentAgents: 5,
            maxConcurrentByState: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["open"] = 1
            });

        await using var harness = await TestHarness.CreateAsync(
            workflow,
            tracker: new FakeTrackerClient([BuildIssue("issue-2", "#2", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
    }

    [Fact]
    public async Task RunTickAsync_ShouldRejectTodoIssuesWithActiveBlockers()
    {
        var workflow = BuildWorkflowDefinition(
            maxConcurrentAgents: 1,
            activeStates: ["Todo"]);

        var todoIssue = BuildIssue(
            "issue-1",
            "#1",
            "Todo",
            [new BlockerRef("issue-0", "#0", "Open")]);

        await using var harness = await TestHarness.CreateAsync(
            workflow,
            tracker: new FakeTrackerClient([todoIssue]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.Success));

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.Coordinator.StartRequests);
    }

    [Fact]
    public async Task RunTickAsync_ShouldStopTerminalRunsAndCleanupWorkspace()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Closed"
            }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning, stopReturnsFalse: true));

        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Single(harness.WorkspaceManager.CleanupRequests);
        Assert.Equal(RunStatusNames.CanceledByReconciliation, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Equal(RunStatusNames.CanceledByReconciliation, (await harness.DbContext.DispatchClaims.SingleAsync()).Status);
    }

    [Fact]
    public async Task RunTickAsync_ShouldPersistTerminalStopBeforeCancelingLiveRun()
    {
        var coordinator = new FakeIssueExecutionCoordinator(
            FakeDispatchOutcome.LeaveRunning,
            observeStopStateWithFreshContext: true);

        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Closed"
            }),
            coordinator);

        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.NotNull(coordinator.ObservedStopState);
        Assert.Equal(RunStopReasons.Terminal, coordinator.ObservedStopState.Value.RequestedStopReason);
        Assert.True(coordinator.ObservedStopState.Value.CleanupWorkspaceOnStop);
    }

    [Fact]
    public async Task RunTickAsync_ShouldRefreshTrackedIssueCacheStateForClosedIssues()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Closed"
            }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        var initialCachedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5);
        await harness.InsertIssueCacheAsync("issue-1", "#1", "Open", initialCachedAtUtc);

        await harness.Service.RunTickAsync(CancellationToken.None);

        var cachedIssue = await harness.DbContext.IssueCache.SingleAsync();
        Assert.Equal("Closed", cachedIssue.State);
        Assert.True(cachedIssue.CachedAtUtc > initialCachedAtUtc);
    }

    [Fact]
    public async Task RunTickAsync_ShouldCleanupRetryWorkspaceWhenTrackedIssueBecomesClosed()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Closed"
            }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertIssueCacheAsync("issue-1", "#1", "Open", DateTimeOffset.UtcNow.AddMinutes(-5));
        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        var cleanupRequest = Assert.Single(harness.WorkspaceManager.CleanupRequests);
        Assert.Equal("#1", cleanupRequest.IssueIdentifier);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Equal(RunStatusNames.CanceledByReconciliation, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Equal(RunStatusNames.CanceledByReconciliation, (await harness.DbContext.DispatchClaims.SingleAsync()).Status);
        Assert.NotNull((await harness.DbContext.WorkspaceRecords.SingleAsync()).LastCleanedAtUtc);
    }

    [Fact]
    public async Task RunTickAsync_ShouldResetLastReportedTokenTotalsWhenRetryStartsNewAttempt()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([BuildIssue("issue-1", "#1", "Open", null)]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRetryingRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            inputTokens: 100,
            outputTokens: 50,
            totalTokens: 150,
            lastReportedInputTokens: 100,
            lastReportedOutputTokens: 50,
            lastReportedTotalTokens: 150);

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        Assert.Equal(RunStatusNames.Running, run.Status);
        Assert.Equal(100, run.InputTokens);
        Assert.Equal(50, run.OutputTokens);
        Assert.Equal(150, run.TotalTokens);
        Assert.Equal(0, run.LastReportedInputTokens);
        Assert.Equal(0, run.LastReportedOutputTokens);
        Assert.Equal(0, run.LastReportedTotalTokens);
    }

    [Fact]
    public async Task RunTickAsync_ShouldStopNonActiveRunsWithoutCleanup()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([], issueStatesById: new Dictionary<string, string>
            {
                ["issue-1"] = "Blocked"
            }),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning, stopReturnsFalse: true));

        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-1");

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(harness.WorkspaceManager.CleanupRequests);
        Assert.Equal(RunStatusNames.CanceledByReconciliation, (await harness.DbContext.Runs.SingleAsync()).Status);
    }

    [Fact]
    public async Task RunTickAsync_ShouldDetectStalledRunsFromLastActivity()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning, stopReturnsFalse: true));

        await harness.InsertRunningRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            startedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
            lastEventAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10));

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        var retry = await harness.DbContext.RetryQueue.SingleAsync();
        Assert.Equal(RunStatusNames.Retrying, run.Status);
        Assert.Equal(1, retry.Attempt);
    }

    [Fact]
    public async Task RunTickAsync_ShouldReconcileBeforeSkippingInvalidDispatch()
    {
        var workflow = BuildWorkflowDefinition(maxConcurrentAgents: 1, apiKey: "$MISSING_TOKEN");

        await using var harness = await TestHarness.CreateAsync(
            workflow,
            tracker: new FakeTrackerClient([]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning, stopReturnsFalse: true));

        await harness.InsertRunningRunAsync(
            "issue-1",
            "#1",
            "Open",
            "instance-1",
            startedAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
            lastEventAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10));

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.False(harness.Tracker.FetchCandidateIssuesCalled);
        Assert.Equal(RunStatusNames.Retrying, (await harness.DbContext.Runs.SingleAsync()).Status);
    }

    [Fact]
    public async Task RunTickAsync_ShouldNotRecoverRunsOwnedByInstancesWithLiveLease()
    {
        await using var harness = await TestHarness.CreateAsync(
            BuildWorkflowDefinition(maxConcurrentAgents: 1),
            tracker: new FakeTrackerClient([]),
            coordinator: new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));

        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-2");
        await harness.InsertLeaseAsync("poll-dispatch", "instance-2", DateTimeOffset.UtcNow.AddMinutes(5));

        await harness.Service.RunTickAsync(CancellationToken.None);

        var run = await harness.DbContext.Runs.SingleAsync();
        var claim = await harness.DbContext.DispatchClaims.SingleAsync();

        Assert.Equal("instance-2", run.OwnerInstanceId);
        Assert.Equal(RunStatusNames.Running, run.Status);
        Assert.Equal("instance-2", claim.ClaimedByInstanceId);
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
    }

    private static WorkflowDefinition BuildWorkflowDefinition(
        int maxConcurrentAgents,
        IReadOnlyList<string>? activeStates = null,
        IReadOnlyDictionary<string, int>? maxConcurrentByState = null,
        string apiKey = "test-token")
    {
        var runtime = new WorkflowRuntimeSettings(
            new WorkflowTrackerSettings(
                Kind: "github",
                Endpoint: "https://api.github.com/graphql",
                ApiKey: apiKey,
                Owner: "released",
                Repo: "symphony",
                Milestone: null,
                IncludePullRequests: true,
                Labels: [],
                ActiveStates: activeStates ?? ["Open"],
                TerminalStates: ["Closed"]),
            new WorkflowPollingSettings(600_000),
            new WorkflowAgentSettings(
                MaxConcurrentAgents: maxConcurrentAgents,
                MaxTurns: 20,
                MaxRetryBackoffMs: 300_000,
                MaxConcurrentAgentsByState: maxConcurrentByState ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)),
            new WorkflowServerSettings(Port: null),
            new WorkflowWorkspaceSettings("./workspaces", "./workspaces/repo", "./workspaces/worktrees", "main", null),
            new WorkflowHooksSettings(null, null, null, null, 60_000),
            new WorkflowCodexSettings("codex app-server", 30_000, "never", "danger-full-access", "danger-full-access", 5_000, 300_000));

        return new WorkflowDefinition(new Dictionary<string, object?>(), "Prompt body", runtime, "WORKFLOW.md", DateTimeOffset.UtcNow);
    }

    private static NormalizedIssue BuildIssue(string id, string identifier, string state, IReadOnlyList<BlockerRef>? blockedBy)
    {
        return new NormalizedIssue(
            id,
            identifier,
            $"Issue {identifier}",
            null,
            1,
            state,
            null,
            null,
            null,
            [],
            [],
            blockedBy ?? [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    private sealed class TestHarness : IAsyncDisposable
    {
        private readonly string dbPath;

        private TestHarness(
            string dbPath,
            SymphonyDbContext dbContext,
            FakeTrackerClient tracker,
            FakeWorkspaceManager workspaceManager,
            FakeIssueExecutionCoordinator coordinator,
            OrchestrationTickService service)
        {
            this.dbPath = dbPath;
            DbContext = dbContext;
            Tracker = tracker;
            WorkspaceManager = workspaceManager;
            Coordinator = coordinator;
            Service = service;
        }

        public SymphonyDbContext DbContext { get; }
        public FakeTrackerClient Tracker { get; }
        public FakeWorkspaceManager WorkspaceManager { get; }
        public FakeIssueExecutionCoordinator Coordinator { get; }
        public OrchestrationTickService Service { get; }

        public static async Task<TestHarness> CreateAsync(
            WorkflowDefinition workflowDefinition,
            FakeTrackerClient tracker,
            FakeIssueExecutionCoordinator coordinator)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-orchestration.db");
            var options = new DbContextOptionsBuilder<SymphonyDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            var dbContext = new SymphonyDbContext(options);
            await dbContext.Database.EnsureDeletedAsync();
            await dbContext.Database.EnsureCreatedAsync();

            var workspaceManager = new FakeWorkspaceManager();
            coordinator.Attach(dbContext, dbPath);

            var service = new OrchestrationTickService(
                new FakeWorkflowDefinitionProvider(workflowDefinition),
                tracker,
                new OrchestrationCoordinationStore(dbContext, TimeProvider.System),
                dbContext,
                workspaceManager,
                coordinator,
                Options.Create(new OrchestrationOptions
                {
                    InstanceId = "instance-1",
                    LeaseName = "poll-dispatch",
                    LeaseTtlSeconds = 900
                }),
                TimeProvider.System,
                NullLogger<OrchestrationTickService>.Instance);

            return new TestHarness(dbPath, dbContext, tracker, workspaceManager, coordinator, service);
        }

        public async Task InsertRunningRunAsync(
            string issueId,
            string identifier,
            string state,
            string instanceId,
            DateTimeOffset? startedAtUtc = null,
            DateTimeOffset? lastEventAtUtc = null)
        {
            var run = new RunEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                IssueId = issueId,
                IssueIdentifier = identifier,
                OwnerInstanceId = instanceId,
                Status = RunStatusNames.Running,
                State = state,
                StartedAtUtc = startedAtUtc ?? DateTimeOffset.UtcNow
            };
            run.LastEventAtUtc = lastEventAtUtc;

            DbContext.Runs.Add(run);
            DbContext.RunAttempts.Add(new RunAttemptEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                RunId = run.Id,
                IssueId = issueId,
                Status = RunStatusNames.Running,
                StartedAtUtc = run.StartedAtUtc
            });
            DbContext.DispatchClaims.Add(new DispatchClaimEntity
            {
                IssueId = issueId,
                IssueIdentifier = identifier,
                ClaimedByInstanceId = instanceId,
                ClaimedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Status = "active"
            });

            await DbContext.SaveChangesAsync();
        }

        public async Task InsertLeaseAsync(string leaseName, string ownerInstanceId, DateTimeOffset expiresAtUtc)
        {
            DbContext.InstanceLeases.Add(new InstanceLeaseEntity
            {
                LeaseName = leaseName,
                OwnerInstanceId = ownerInstanceId,
                AcquiredAtUtc = expiresAtUtc.AddMinutes(-5),
                ExpiresAtUtc = expiresAtUtc,
                UpdatedAtUtc = expiresAtUtc.AddMinutes(-1)
            });

            await DbContext.SaveChangesAsync();
        }

        public async Task InsertIssueCacheAsync(
            string issueId,
            string identifier,
            string state,
            DateTimeOffset? cachedAtUtc = null)
        {
            var nowUtc = cachedAtUtc ?? DateTimeOffset.UtcNow;
            DbContext.IssueCache.Add(new IssueCacheEntity
            {
                IssueId = issueId,
                Identifier = identifier,
                Title = $"Issue {identifier}",
                State = state,
                LabelsJson = "[]",
                PullRequestsJson = "[]",
                BlockedByJson = "[]",
                CachedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            });

            await DbContext.SaveChangesAsync();
        }

        public async Task InsertRetryingRunAsync(
            string issueId,
            string identifier,
            string state,
            string instanceId,
            int inputTokens = 0,
            int outputTokens = 0,
            int totalTokens = 0,
            int lastReportedInputTokens = 0,
            int lastReportedOutputTokens = 0,
            int lastReportedTotalTokens = 0)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var run = new RunEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                IssueId = issueId,
                IssueIdentifier = identifier,
                OwnerInstanceId = instanceId,
                Status = RunStatusNames.Retrying,
                State = state,
                CurrentRetryAttempt = 1,
                StartedAtUtc = nowUtc.AddMinutes(-1),
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = totalTokens,
                LastReportedInputTokens = lastReportedInputTokens,
                LastReportedOutputTokens = lastReportedOutputTokens,
                LastReportedTotalTokens = lastReportedTotalTokens
            };

            DbContext.Runs.Add(run);
            DbContext.RetryQueue.Add(new RetryQueueEntity
            {
                IssueId = issueId,
                IssueIdentifier = identifier,
                RunId = run.Id,
                OwnerInstanceId = instanceId,
                Attempt = 1,
                DueAtUtc = nowUtc.AddSeconds(-1),
                DelayType = RetryDelayTypes.Continuation,
                MaxBackoffMs = 300_000,
                CreatedAtUtc = nowUtc.AddMinutes(-1),
                UpdatedAtUtc = nowUtc.AddMinutes(-1)
            });
            DbContext.DispatchClaims.Add(new DispatchClaimEntity
            {
                IssueId = issueId,
                IssueIdentifier = identifier,
                ClaimedByInstanceId = instanceId,
                ClaimedAtUtc = nowUtc.AddMinutes(-1),
                UpdatedAtUtc = nowUtc.AddMinutes(-1),
                Status = "active"
            });
            DbContext.WorkspaceRecords.Add(new WorkspaceRecordEntity
            {
                IssueId = issueId,
                IssueIdentifier = identifier,
                WorkspacePath = $"C:\\tmp\\{identifier}",
                BranchName = $"symphony/{identifier}",
                LastPreparedAtUtc = nowUtc.AddMinutes(-1)
            });

            await DbContext.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            TryDeleteFile(dbPath);
            TryDeleteFile($"{dbPath}-wal");
            TryDeleteFile($"{dbPath}-shm");
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FakeWorkflowDefinitionProvider(WorkflowDefinition workflowDefinition) : IWorkflowDefinitionProvider
    {
        public Task<WorkflowDefinition> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(workflowDefinition);
    }

    private sealed class FakeTrackerClient(
        IReadOnlyList<NormalizedIssue> issues,
        IReadOnlyDictionary<string, string>? issueStatesById = null,
        bool matchesCandidateFilters = true) : IGitHubTrackerClient
    {
        private readonly Dictionary<string, string> statesById = issueStatesById is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(issueStatesById, StringComparer.OrdinalIgnoreCase);

        public bool FetchCandidateIssuesCalled { get; private set; }

        public Task<IReadOnlyList<NormalizedIssue>> FetchCandidateIssuesAsync(TrackerQuery query, CancellationToken cancellationToken = default)
        {
            FetchCandidateIssuesCalled = true;
            return Task.FromResult(issues);
        }

        public Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByStatesAsync(TrackerQuery query, IReadOnlyList<string> states, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NormalizedIssue>>([]);

        public Task<IReadOnlyList<IssueStateSnapshot>> FetchIssueStatesByIdsAsync(TrackerQuery query, IReadOnlyList<string> issueIds, CancellationToken cancellationToken = default)
        {
            var snapshots = issueIds
                .Where(id => statesById.ContainsKey(id))
                .Select(id => new IssueStateSnapshot(id, statesById[id], matchesCandidateFilters))
                .ToList();
            return Task.FromResult<IReadOnlyList<IssueStateSnapshot>>(snapshots);
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

    private sealed class FakeWorkspaceManager : IWorkspaceManager
    {
        public List<WorkspaceCleanupRequest> CleanupRequests { get; } = [];

        public Task<WorkspacePreparationResult> PrepareIssueWorkspaceAsync(WorkspacePreparationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspacePreparationResult($"C:\\tmp\\{request.IssueIdentifier}", request.SuggestedBranchName ?? "branch", CreatedNow: true));

        public Task<WorkspaceCleanupResult> CleanupIssueWorkspaceAsync(WorkspaceCleanupRequest request, CancellationToken cancellationToken = default)
        {
            CleanupRequests.Add(request);
            return Task.FromResult(new WorkspaceCleanupResult($"C:\\tmp\\{request.IssueIdentifier}", Existed: true, RemovedNow: true));
        }
    }

    private enum FakeDispatchOutcome
    {
        LeaveRunning,
        Success,
        Failure
    }

    private sealed class FakeIssueExecutionCoordinator(
        FakeDispatchOutcome outcome,
        bool stopReturnsFalse = false,
        bool observeStopStateWithFreshContext = false) : IIssueExecutionCoordinator
    {
        private SymphonyDbContext? dbContext;
        private string? dbPath;

        public List<IssueExecutionRequest> StartRequests { get; } = [];
        public List<string> StopRequests { get; } = [];
        public (string? RequestedStopReason, bool CleanupWorkspaceOnStop)? ObservedStopState { get; private set; }

        public void Attach(SymphonyDbContext dbContext, string dbPath)
        {
            this.dbContext = dbContext;
            this.dbPath = dbPath;
        }

        public async Task<bool> TryStartAsync(IssueExecutionRequest request, CancellationToken cancellationToken = default)
        {
            StartRequests.Add(request);
            if (dbContext is null || outcome == FakeDispatchOutcome.LeaveRunning)
            {
                return true;
            }

            var nowUtc = DateTimeOffset.UtcNow;
            var run = await dbContext.Runs.SingleAsync(runEntity => runEntity.Id == request.RunId, cancellationToken);
            var attempt = await dbContext.RunAttempts.SingleAsync(attemptEntity => attemptEntity.Id == request.AttemptId, cancellationToken);

            if (outcome == FakeDispatchOutcome.Success)
            {
                run.Status = RunStatusNames.Retrying;
                run.CurrentRetryAttempt = 1;
                attempt.Status = RunStatusNames.Succeeded;
                attempt.CompletedAtUtc = nowUtc;
                dbContext.RetryQueue.Add(new RetryQueueEntity
                {
                    IssueId = request.Issue.Id,
                    IssueIdentifier = request.Issue.Identifier,
                    RunId = request.RunId,
                    OwnerInstanceId = request.InstanceId,
                    Attempt = 1,
                    DueAtUtc = nowUtc.AddSeconds(1),
                    DelayType = RetryDelayTypes.Continuation,
                    MaxBackoffMs = request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc
                });
            }
            else
            {
                var retryAttempt = request.Attempt.HasValue ? request.Attempt.Value + 1 : 1;
                run.Status = RunStatusNames.Retrying;
                run.CurrentRetryAttempt = retryAttempt;
                attempt.Status = RunStatusNames.Failed;
                attempt.Error = "simulated failure";
                attempt.CompletedAtUtc = nowUtc;
                dbContext.RetryQueue.Add(new RetryQueueEntity
                {
                    IssueId = request.Issue.Id,
                    IssueIdentifier = request.Issue.Identifier,
                    RunId = request.RunId,
                    OwnerInstanceId = request.InstanceId,
                    Attempt = retryAttempt,
                    DueAtUtc = nowUtc.AddSeconds(10),
                    DelayType = RetryDelayTypes.Backoff,
                    Error = "simulated failure",
                    MaxBackoffMs = request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        public async Task<bool> TryStopAsync(string issueId, CancellationToken cancellationToken = default)
        {
            StopRequests.Add(issueId);

            if (observeStopStateWithFreshContext && dbPath is not null)
            {
                var options = new DbContextOptionsBuilder<SymphonyDbContext>()
                    .UseSqlite($"Data Source={dbPath}")
                    .Options;
                await using var freshDbContext = new SymphonyDbContext(options);
                var run = await freshDbContext.Runs.SingleAsync(
                    runEntity => runEntity.IssueId == issueId,
                    cancellationToken);
                ObservedStopState = (run.RequestedStopReason, run.CleanupWorkspaceOnStop);
            }

            return !stopReturnsFalse;
        }
    }
}
