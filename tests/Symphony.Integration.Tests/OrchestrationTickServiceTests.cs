using Microsoft.EntityFrameworkCore;
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
            coordinator.Attach(dbContext);

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

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
        }
    }

    private sealed class FakeWorkflowDefinitionProvider(WorkflowDefinition workflowDefinition) : IWorkflowDefinitionProvider
    {
        public Task<WorkflowDefinition> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(workflowDefinition);
    }

    private sealed class FakeTrackerClient(
        IReadOnlyList<NormalizedIssue> issues,
        IReadOnlyDictionary<string, string>? issueStatesById = null) : IGitHubTrackerClient
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
                .Select(id => new IssueStateSnapshot(id, statesById[id]))
                .ToList();
            return Task.FromResult<IReadOnlyList<IssueStateSnapshot>>(snapshots);
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

    private sealed class FakeIssueExecutionCoordinator(FakeDispatchOutcome outcome, bool stopReturnsFalse = false) : IIssueExecutionCoordinator
    {
        private SymphonyDbContext? dbContext;

        public List<IssueExecutionRequest> StartRequests { get; } = [];
        public List<string> StopRequests { get; } = [];

        public void Attach(SymphonyDbContext dbContext)
        {
            this.dbContext = dbContext;
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

        public Task<bool> TryStopAsync(string issueId, CancellationToken cancellationToken = default)
        {
            StopRequests.Add(issueId);
            return Task.FromResult(!stopReturnsFalse);
        }
    }
}
