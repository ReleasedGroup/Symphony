using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Symphony.Core.Abstractions;
using Symphony.Core.Configuration;
using Symphony.Core.Models;
using Symphony.Host.Services;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Workflows;

namespace Symphony.Integration.Tests;

public sealed partial class OrchestrationTickServiceTests
{
    [Fact]
    [Trait("Spec", "17.4")]
    public async Task CleanupReservation_ShouldBlockSameIssueDispatchWithoutBlockingUnrelatedWriters()
    {
        await using var harness = await TestHarness.CreateAsync(BuildWorkflowDefinition(1),
            new FakeTrackerClient([], new Dictionary<string, string> { ["issue-1"] = "Closed" }),
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        await harness.InsertIssueCacheAsync("issue-1", "#1", "Open");
        var callbackCompleted = false;
        harness.WorkspaceManager.OnCleanup = () =>
        {
            using var otherContext = new SymphonyDbContext(new DbContextOptionsBuilder<SymphonyDbContext>()
                .UseSqlite(harness.DbContext.Database.GetConnectionString() + ";Default Timeout=1;Pooling=False").Options);
            var store = new OrchestrationCoordinationStore(otherContext, TimeProvider.System);
            Assert.False(store.TryClaimIssueAsync("issue-1", "#1", "poll-dispatch", "instance-1").GetAwaiter().GetResult());
            Assert.False(store.TryClaimIssueAsync("issue-1", "#1", "poll-dispatch", "instance-2").GetAwaiter().GetResult());
            Assert.True(store.TryClaimIssueAsync("unrelated", "#2", "poll-dispatch", "instance-2").GetAwaiter().GetResult());
            otherContext.DispatchClaims.Add(new DispatchClaimEntity
            {
                IssueId = "issue-1", IssueIdentifier = "#1", ClaimedByInstanceId = "instance-2", Status = "active"
            });
            Assert.Throws<DbUpdateException>(() => otherContext.SaveChanges());
            callbackCompleted = true;
        };

        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.True(callbackCompleted);
        Assert.Empty(await harness.DbContext.IssueCache.ToListAsync());
        Assert.Equal("unrelated", (await harness.DbContext.DispatchClaims.SingleAsync()).IssueId);
    }

    [Fact]
    [Trait("Spec", "17.4")]
    public async Task ReopeningAfterFailedCleanup_ShouldReleaseReservationBeforeDispatch()
    {
        var issues = new List<NormalizedIssue>();
        await using var harness = await TestHarness.CreateAsync(BuildWorkflowDefinition(1),
            new FakeTrackerClient(issues, new Dictionary<string, string> { ["issue-1"] = "Closed" }),
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        await harness.InsertIssueCacheAsync("issue-1", "#1", "Open");
        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", totalTokens: 150);
        harness.WorkspaceManager.WorkspaceRemains = true;
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Single(await harness.DbContext.DispatchClaims.Where(row => row.Status == "cleanup").ToListAsync());
        issues.Add(BuildIssue("issue-1", "#1", "Open", null));
        harness.Tracker.SetIssueState("issue-1", "Open");
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Single(harness.Coordinator.StartRequests);
        Assert.Empty(await harness.DbContext.DispatchClaims.Where(row => row.Status == "cleanup").ToListAsync());
        Assert.Single(await harness.DbContext.DispatchClaims.Where(row => row.Status == "active").ToListAsync());
        Assert.Equal(0, (await harness.DbContext.RuntimeTotals.AsNoTracking().SingleAsync()).TotalTokens);
        Assert.Equal(150, await harness.DbContext.Runs.SumAsync(row => row.TotalTokens));
    }

    [Fact]
    [Trait("Spec", "17.4")]
    public async Task RetentionMigration_ShouldUpgradeExistingDatabaseWithoutDroppingHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new SymphonyDbContext(new DbContextOptionsBuilder<SymphonyDbContext>()
            .UseSqlite(connection).Options);
        await db.GetService<IMigrator>().MigrateAsync("20260928003322_ManagedDispatchLease");
        db.Runs.Add(new RunEntity
        {
            Id = "legacy", IssueId = "issue-1", IssueIdentifier = "#1", State = "Closed",
            Status = RunStatusNames.Succeeded, TotalTokens = 150
        });
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(150, (await db.Runs.SingleAsync()).TotalTokens);
        Assert.Equal(0, (await db.RuntimeTotals.SingleAsync()).TotalTokens);
    }

    [Theory]
    [Trait("Spec", "17.4")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WorkerFinalization_ShouldReleaseTerminalRunEvenWhenWorkspaceCleanupFails(bool throws)
    {
        var root = Directory.CreateTempSubdirectory("symphony-terminal-finalization-").FullName;
        try
        {
            var workflow = BuildWorkflowDefinition(1);
            workflow = workflow with { Runtime = workflow.Runtime with
            {
                Workspace = workflow.Runtime.Workspace with { Root = root }
            } };
            using var cancellation = new CancellationTokenSource();
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Services.AddDbContext<SymphonyDbContext>(options => options
                .UseSqlite($"Data Source={Path.Combine(root, "test.db")};Pooling=False"));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(Options.Create(new ManagedLeaseOptions()));
            builder.Services.AddSingleton<ManagedLeaseRuntime>();
            builder.Services.AddScoped<IOrchestrationCoordinationStore, OrchestrationCoordinationStore>();
            builder.Services.AddSingleton<IWorkspaceManager>(new FinalizationWorkspaceManager(root, throws));
            builder.Services.AddSingleton<IWorkspaceHookRunner, NoOpHookRunner>();
            builder.Services.AddSingleton<IWorkflowPromptRenderer, WorkflowPromptRenderer>();
            builder.Services.AddSingleton<IAgentRunner>(new TerminalCancellationRunner(cancellation.Cancel));
            builder.Services.AddSingleton<IssueExecutionCoordinator>();
            using var host = builder.Build();
            await using var scope = host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SymphonyDbContext>();
            await db.Database.MigrateAsync();
            db.Runs.Add(new RunEntity
            {
                Id = "run-1", IssueId = "issue-1", IssueIdentifier = "#1", State = "Closed",
                Status = RunStatusNames.Running, RequestedStopReason = RunStopReasons.Terminal, CleanupWorkspaceOnStop = true
            });
            db.RunAttempts.Add(new RunAttemptEntity
            {
                Id = "attempt-1", RunId = "run-1", IssueId = "issue-1", Status = RunStatusNames.Running
            });
            db.DispatchClaims.Add(new DispatchClaimEntity
            {
                IssueId = "issue-1", IssueIdentifier = "#1", ClaimedByInstanceId = "instance-1"
            });
            await db.SaveChangesAsync();
            var coordinator = host.Services.GetRequiredService<IssueExecutionCoordinator>();
            var request = new IssueExecutionRequest("run-1", "attempt-1", "instance-1", null,
                BuildIssue("issue-1", "#1", "Open", null), workflow);
            var execute = typeof(IssueExecutionCoordinator).GetMethod("ExecuteRunAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)execute.Invoke(coordinator, [request, cancellation])!;
            db.ChangeTracker.Clear();

            Assert.Equal(RunStatusNames.CanceledByReconciliation, (await db.Runs.SingleAsync()).Status);
            Assert.NotNull((await db.RunAttempts.SingleAsync()).CompletedAtUtc);
            Assert.NotNull((await db.DispatchClaims.SingleAsync()).ReleasedAtUtc);
            Assert.Empty(await db.RetryQueue.ToListAsync());
            Assert.Null((await db.WorkspaceRecords.SingleAsync()).LastCleanedAtUtc);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FinalizationWorkspaceManager(string root, bool throws) : IWorkspaceManager
    {
        public Task<WorkspacePreparationResult> PrepareIssueWorkspaceAsync(
            WorkspacePreparationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkspacePreparationResult(root, "branch", false));

        public Task<WorkspaceCleanupResult> CleanupIssueWorkspaceAsync(
            WorkspaceCleanupRequest request, CancellationToken cancellationToken = default) => throws
                ? throw new IOException("Busy workspace")
                : Task.FromResult(new WorkspaceCleanupResult(root, true, false));
    }

    private sealed class TerminalCancellationRunner(Action cancel) : IAgentRunner
    {
        public Task<AgentRunResult> RunIssueAsync(AgentRunRequest request,
            Func<AgentRunUpdate, CancellationToken, Task>? onUpdate = null, CancellationToken cancellationToken = default)
        {
            cancel();
            return Task.FromCanceled<AgentRunResult>(cancellationToken);
        }
    }

    [Fact]
    [Trait("Spec", "17.4")]
    [Trait("Spec", "17.6")]
    public async Task TerminalCleanup_ShouldRemoveAllCorrelatedHistoryAndPreserveGlobalAccounting()
    {
        await using var harness = await TestHarness.CreateAsync(BuildWorkflowDefinition(1),
            new FakeTrackerClient([], new Dictionary<string, string> { ["closed"] = "Closed", ["open"] = "Open" }),
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        await harness.InsertIssueCacheAsync("closed", "#1", "Open");
        await harness.InsertRetryingRunAsync("closed", "#1", "Open", "instance-1", 100, 50, 150);
        await harness.InsertIssueCacheAsync("open", "#10", "Open");
        await harness.InsertRunningRunAsync("open", "#10", "Open", "instance-1");
        await SeedHistoryAsync(harness, "closed");
        harness.DbContext.EventLog.Add(new EventLogEntity
        {
            IssueId = "open", IssueIdentifier = "#10", EventName = "open_event", Level = "Information", Message = "Keep me"
        });
        await harness.DbContext.SaveChangesAsync();

        var before = JsonSerializer.SerializeToElement(await new RuntimeStateService(harness.DbContext, TimeProvider.System)
            .GetStateAsync(CancellationToken.None)).GetProperty("codex_totals");
        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Equal("open", (await harness.DbContext.IssueCache.SingleAsync()).IssueId);
        Assert.Equal("open", (await harness.DbContext.Runs.SingleAsync()).IssueId);
        Assert.Equal("open", (await harness.DbContext.RunAttempts.SingleAsync()).IssueId);
        Assert.Empty(await harness.DbContext.Sessions.ToListAsync());
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Empty(await harness.DbContext.WorkspaceRecords.ToListAsync());
        Assert.Equal("open", (await harness.DbContext.DispatchClaims.SingleAsync()).IssueId);
        Assert.Equal("open_event", (await harness.DbContext.EventLog.SingleAsync()).EventName);
        var totals = await harness.DbContext.RuntimeTotals.AsNoTracking().SingleAsync();
        Assert.Equal(100, totals.InputTokens);
        Assert.Equal(50, totals.OutputTokens);
        Assert.Equal(150, totals.TotalTokens);
        Assert.Equal(60, totals.SecondsRunning);
        Assert.Equal("{\"remaining\":42}", totals.RateLimitsJson);

        // A fresh context proves the deleted issue stays absent after restart and
        // accounting is durable, even though all of its events and sessions are gone.
        await using var fresh = new SymphonyDbContext(new DbContextOptionsBuilder<SymphonyDbContext>()
            .UseSqlite(harness.DbContext.Database.GetConnectionString()).Options);
        var runtime = new RuntimeStateService(fresh, TimeProvider.System);
        Assert.False((await runtime.GetIssueStateAsync("#1", CancellationToken.None)).Found);
        var after = JsonSerializer.SerializeToElement(await runtime.GetStateAsync(CancellationToken.None));
        Assert.Equal(before.GetProperty("total_tokens").GetInt64(), after.GetProperty("codex_totals").GetProperty("total_tokens").GetInt64());
        Assert.Equal(42, after.GetProperty("rate_limits").GetProperty("remaining").GetInt32());
        Assert.DoesNotContain("closed", after.GetRawText(), StringComparison.Ordinal);
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Equal(150, (await fresh.RuntimeTotals.AsNoTracking().SingleAsync()).TotalTokens);
        Assert.DoesNotContain("closed", harness.Tracker.RefreshRequests.Last());
    }

    [Theory]
    [Trait("Spec", "17.4")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TerminalCleanup_ShouldRetainRecoveryStateUntilWorkspaceRemovalSucceeds(bool throws)
    {
        await using var harness = await TestHarness.CreateAsync(BuildWorkflowDefinition(1),
            new FakeTrackerClient([], new Dictionary<string, string> { ["issue-1"] = "Closed" }),
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        await harness.InsertIssueCacheAsync("issue-1", "#1", "Open");
        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", totalTokens: 150);
        await SeedHistoryAsync(harness, "issue-1");
        harness.WorkspaceManager.CleanupException = throws ? new IOException("Busy workspace") : null;
        harness.WorkspaceManager.WorkspaceRemains = !throws;

        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Equal("Closed", (await harness.DbContext.IssueCache.SingleAsync()).State);
        Assert.Equal(RunStatusNames.CanceledByReconciliation, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Single(await harness.DbContext.WorkspaceRecords.ToListAsync());
        Assert.Single(await harness.DbContext.Sessions.ToListAsync());
        Assert.NotEmpty(await harness.DbContext.EventLog.ToListAsync());
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Equal(0, (await harness.DbContext.RuntimeTotals.AsNoTracking().SingleAsync()).TotalTokens);

        harness.WorkspaceManager.CleanupException = null;
        harness.WorkspaceManager.WorkspaceRemains = false;
        await harness.Service.RunTickAsync(CancellationToken.None);

        Assert.Empty(await harness.DbContext.IssueCache.ToListAsync());
        Assert.Empty(await harness.DbContext.Runs.ToListAsync());
        Assert.Equal(150, (await harness.DbContext.RuntimeTotals.AsNoTracking().SingleAsync()).TotalTokens);
        Assert.Empty(harness.Coordinator.StartRequests);
    }

    [Fact]
    [Trait("Spec", "17.4")]
    public async Task TerminalCleanup_ShouldRollBackDeletionAndAccountingTogether()
    {
        await using var harness = await TestHarness.CreateAsync(BuildWorkflowDefinition(1),
            new FakeTrackerClient([], new Dictionary<string, string> { ["issue-1"] = "Closed" }),
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        await harness.InsertIssueCacheAsync("issue-1", "#1", "Open");
        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", totalTokens: 150);
        await SeedHistoryAsync(harness, "issue-1");
        await harness.DbContext.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER reject_cleanup BEFORE DELETE ON issues_cache
            BEGIN SELECT RAISE(ABORT, 'test cleanup failure'); END;
            """);

        await Assert.ThrowsAsync<SqliteException>(() => harness.Service.RunTickAsync(CancellationToken.None));
        harness.DbContext.ChangeTracker.Clear();
        Assert.Single(await harness.DbContext.Runs.ToListAsync());
        Assert.Single(await harness.DbContext.Sessions.ToListAsync());
        Assert.NotEmpty(await harness.DbContext.EventLog.ToListAsync());
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Equal("cleanup", (await harness.DbContext.DispatchClaims.SingleAsync(row => row.Status == "cleanup")).Status);
        Assert.Equal(0, (await harness.DbContext.RuntimeTotals.AsNoTracking().SingleAsync()).TotalTokens);
        await harness.DbContext.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_cleanup;");
        // Workspace may already be absent after an interrupted/failed DB commit.
        harness.WorkspaceManager.WorkspaceAbsent = true;
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Empty(await harness.DbContext.Runs.ToListAsync());
        Assert.Equal(150, (await harness.DbContext.RuntimeTotals.AsNoTracking().SingleAsync()).TotalTokens);
    }

    [Fact]
    [Trait("Spec", "17.4")]
    public async Task TerminalCleanup_ShouldPropagateCancellationAndRetainState()
    {
        using var cancellation = new CancellationTokenSource();
        await using var harness = await TestHarness.CreateAsync(BuildWorkflowDefinition(1),
            new FakeTrackerClient([], new Dictionary<string, string> { ["issue-1"] = "Closed" }),
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        await harness.InsertIssueCacheAsync("issue-1", "#1", "Open");
        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1");
        harness.WorkspaceManager.OnCleanup = cancellation.Cancel;
        harness.WorkspaceManager.CleanupException = new OperationCanceledException(cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Service.RunTickAsync(cancellation.Token));
        harness.DbContext.ChangeTracker.Clear();
        Assert.Single(await harness.DbContext.Runs.ToListAsync());
        Assert.Single(await harness.DbContext.WorkspaceRecords.ToListAsync());
        Assert.Empty(await harness.DbContext.RetryQueue.ToListAsync());
        Assert.Single(await harness.DbContext.DispatchClaims.Where(row => row.Status == "cleanup").ToListAsync());
    }

    [Fact]
    [Trait("Spec", "17.4")]
    public async Task StartupCleanup_ShouldPruneLegacyHistoryWithoutCacheOrWorkspace()
    {
        await using var harness = await TestHarness.CreateAsync(BuildWorkflowDefinition(1),
            new FakeTrackerClient([BuildIssue("issue-1", "#1", "Closed", null)]),
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        await harness.InsertRetryingRunAsync("issue-1", "#1", "Closed", "instance-1", totalTokens: 15);
        await SeedHistoryAsync(harness, "issue-1");
        harness.WorkspaceManager.WorkspaceAbsent = true;

        Assert.True(await harness.Service.RunStartupCleanupAsync(CancellationToken.None));
        Assert.Empty(await harness.DbContext.Runs.ToListAsync());
        Assert.Empty(await harness.DbContext.WorkspaceRecords.ToListAsync());
        Assert.Empty(await harness.DbContext.EventLog.ToListAsync());
        Assert.Equal(15, (await harness.DbContext.RuntimeTotals.AsNoTracking().SingleAsync()).TotalTokens);
        Assert.True(await harness.Service.RunStartupCleanupAsync(CancellationToken.None));
        Assert.Equal(15, (await harness.DbContext.RuntimeTotals.AsNoTracking().SingleAsync()).TotalTokens);
    }

    [Theory]
    [Trait("Spec", "17.4")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedStartupCleanupOfUntrackedWorkspace_ShouldBeRetriedDuringPolling(bool throws)
    {
        await using var harness = await TestHarness.CreateAsync(BuildWorkflowDefinition(1),
            new FakeTrackerClient([BuildIssue("issue-1", "#1", "Closed", null)],
                new Dictionary<string, string> { ["issue-1"] = "Closed" }),
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        harness.WorkspaceManager.CleanupException = throws ? new IOException("Busy worktree") : null;
        harness.WorkspaceManager.WorkspaceRemains = !throws;
        await harness.Service.RunStartupCleanupAsync(CancellationToken.None);
        Assert.Single(await harness.DbContext.IssueCache.ToListAsync());
        harness.WorkspaceManager.CleanupException = null;
        harness.WorkspaceManager.WorkspaceRemains = false;
        await CreateManagedControl(harness.DbContext).SetPausedAsync(true, CancellationToken.None);
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Empty(await harness.DbContext.IssueCache.ToListAsync());
        Assert.Equal(2, harness.WorkspaceManager.CleanupRequests.Count);
    }

    [Fact]
    [Trait("Spec", "17.4")]
    public async Task StartupCleanup_ShouldKeepLiveWorkerWorkspaceAndHistory()
    {
        await using var harness = await TestHarness.CreateAsync(BuildWorkflowDefinition(1),
            new FakeTrackerClient([BuildIssue("issue-1", "#1", "Closed", null)]),
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-2");
        await harness.Service.RunStartupCleanupAsync(CancellationToken.None);
        Assert.Empty(harness.WorkspaceManager.CleanupRequests);
        Assert.Equal(RunStatusNames.Running, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Single(await harness.DbContext.RunAttempts.ToListAsync());
    }

    [Fact]
    [Trait("Spec", "17.4")]
    public async Task StartupCleanup_ShouldKeepForeignLiveRetryClaims()
    {
        await using var harness = await TestHarness.CreateAsync(BuildWorkflowDefinition(1),
            new FakeTrackerClient([BuildIssue("issue-1", "#1", "Closed", null)]),
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-2");
        await harness.InsertLeaseAsync("owner-heartbeat", "instance-2", DateTimeOffset.UtcNow.AddMinutes(15));
        await harness.Service.RunStartupCleanupAsync(CancellationToken.None);
        Assert.Empty(harness.WorkspaceManager.CleanupRequests);
        Assert.Equal("active", (await harness.DbContext.DispatchClaims.SingleAsync()).Status);
        Assert.Single(await harness.DbContext.RetryQueue.ToListAsync());
    }

    [Fact]
    [Trait("Spec", "17.4")]
    public async Task TickCleanup_ShouldRevisitFinishedLiveRunsWithoutCacheRows()
    {
        await using var harness = await TestHarness.CreateAsync(BuildWorkflowDefinition(1),
            new FakeTrackerClient([], new Dictionary<string, string> { ["issue-1"] = "Closed" }),
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-1");
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Single(await harness.DbContext.Runs.ToListAsync());
        Assert.Empty(harness.WorkspaceManager.CleanupRequests);
        // The worker has now completed finalization after its cancellation request.
        var run = await harness.DbContext.Runs.SingleAsync();
        run.Status = RunStatusNames.CanceledByReconciliation;
        await harness.DbContext.SaveChangesAsync();
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Empty(await harness.DbContext.Runs.ToListAsync());
        Assert.Single(harness.WorkspaceManager.CleanupRequests);
    }

    [Fact]
    [Trait("Spec", "17.4")]
    public async Task PausedDispatch_ShouldStillPruneTerminalIssues()
    {
        await using var harness = await TestHarness.CreateAsync(BuildWorkflowDefinition(1),
            new FakeTrackerClient([], new Dictionary<string, string> { ["issue-1"] = "Closed" }),
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        await harness.InsertIssueCacheAsync("issue-1", "#1", "Open");
        await CreateManagedControl(harness.DbContext).SetPausedAsync(true, CancellationToken.None);
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Empty(await harness.DbContext.IssueCache.ToListAsync());
        Assert.Empty(harness.Coordinator.StartRequests);
        Assert.False(harness.Tracker.FetchCandidateIssuesCalled);
    }

    [Fact]
    [Trait("Spec", "17.4")]
    public async Task ReopenedEligibleIssue_ShouldDispatchAfreshAfterPruning()
    {
        var issues = new List<NormalizedIssue>();
        await using var harness = await TestHarness.CreateAsync(BuildWorkflowDefinition(1),
            new FakeTrackerClient(issues, new Dictionary<string, string> { ["issue-1"] = "Closed" }),
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        await harness.InsertIssueCacheAsync("issue-1", "#1", "Open");
        await harness.InsertRetryingRunAsync("issue-1", "#1", "Open", "instance-1", totalTokens: 150);
        var oldRunId = (await harness.DbContext.Runs.SingleAsync()).Id;
        await harness.Service.RunTickAsync(CancellationToken.None);
        issues.Add(BuildIssue("issue-1", "#1", "Open", null));
        await harness.Service.RunTickAsync(CancellationToken.None);
        var request = Assert.Single(harness.Coordinator.StartRequests);
        Assert.NotEqual(oldRunId, request.RunId);
        Assert.Null(request.Attempt);
        Assert.Equal("Open", (await harness.DbContext.IssueCache.SingleAsync()).State);
        Assert.Equal(RunStatusNames.Running, (await harness.DbContext.Runs.SingleAsync()).Status);
        Assert.Equal(150, (await harness.DbContext.RuntimeTotals.AsNoTracking().SingleAsync()).TotalTokens);
    }

    [Fact]
    [Trait("Spec", "17.6")]
    public async Task CleanupOfOlderIssue_ShouldNotReplaceNewerGlobalRateLimits()
    {
        await using var harness = await TestHarness.CreateAsync(BuildWorkflowDefinition(1),
            new FakeTrackerClient([], new Dictionary<string, string> { ["issue-1"] = "Closed" }),
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        await harness.InsertIssueCacheAsync("issue-1", "#1", "Open");
        harness.DbContext.EventLog.Add(new EventLogEntity
        {
            IssueId = "issue-1", EventName = "rate_limits_updated", Level = "Information", Message = "Old",
            DataJson = "{\"remaining\":42}"
        });
        await harness.DbContext.SaveChangesAsync();
        harness.DbContext.EventLog.Add(new EventLogEntity
        {
            IssueId = "already-pruned", EventName = "rate_limits_updated", Level = "Information", Message = "New",
            DataJson = "{\"remaining\":99}"
        });
        await harness.DbContext.SaveChangesAsync();
        var newer = await harness.DbContext.EventLog.OrderByDescending(row => row.Id).FirstAsync();
        var totals = await harness.DbContext.RuntimeTotals.SingleAsync();
        totals.RateLimitsEventId = newer.Id;
        totals.RateLimitsJson = newer.DataJson;
        harness.DbContext.EventLog.Remove(newer);
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);
        var snapshot = JsonSerializer.SerializeToElement(await new RuntimeStateService(harness.DbContext, TimeProvider.System)
            .GetStateAsync(CancellationToken.None));
        Assert.Equal(99, snapshot.GetProperty("rate_limits").GetProperty("remaining").GetInt32());
        Assert.Equal("{\"remaining\":99}", (await harness.DbContext.RuntimeTotals.AsNoTracking().SingleAsync()).RateLimitsJson);
    }

    [Theory]
    [Trait("Spec", "17.4")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrMissingTrackerRefresh_ShouldKeepAllIssueState(bool refreshFails)
    {
        await using var harness = await TestHarness.CreateAsync(BuildWorkflowDefinition(1),
            new FakeTrackerClient([], refreshFails: refreshFails),
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        await harness.InsertIssueCacheAsync("issue-1", "#1", "Open");
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Single(await harness.DbContext.IssueCache.ToListAsync());
        Assert.Empty(harness.WorkspaceManager.CleanupRequests);
    }

    private static async Task SeedHistoryAsync(TestHarness harness, string issueId)
    {
        var run = await harness.DbContext.Runs.SingleAsync(row => row.IssueId == issueId);
        var attempt = new RunAttemptEntity
        {
            Id = Guid.NewGuid().ToString("N"), RunId = run.Id, IssueId = issueId,
            Status = RunStatusNames.Succeeded, StartedAtUtc = DateTimeOffset.UnixEpoch,
            CompletedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(60)
        };
        harness.DbContext.RunAttempts.Add(attempt);
        var sessionId = Guid.NewGuid().ToString("N");
        harness.DbContext.Sessions.Add(new SessionEntity { Id = sessionId, RunId = run.Id, RunAttemptId = attempt.Id });
        // Include legacy events identified only by run, attempt, session or identifier.
        harness.DbContext.EventLog.AddRange(
            new EventLogEntity { IssueId = issueId, EventName = "rate_limits_updated", Level = "Information", Message = "Usage", DataJson = "{\"remaining\":42}" },
            new EventLogEntity { RunId = run.Id, EventName = "run_event", Level = "Information", Message = "Run" },
            new EventLogEntity { RunAttemptId = attempt.Id, EventName = "attempt_event", Level = "Information", Message = "Attempt" },
            new EventLogEntity { SessionId = sessionId, EventName = "session_event", Level = "Information", Message = "Session" },
            new EventLogEntity { IssueIdentifier = run.IssueIdentifier, EventName = "identifier_event", Level = "Information", Message = "Issue" });
        await harness.DbContext.SaveChangesAsync();
    }
}
