using Microsoft.EntityFrameworkCore;
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
    [Theory]
    [Trait("Spec", "17.4")]
    [InlineData("Closed", "#42", false, true)]
    [InlineData("Closed", null, false, false)]
    [InlineData("Open", "#42", false, false)]
    [InlineData("Closed", "#42", true, false)]
    [InlineData(null, "#42", false, false)]
    public async Task Polling_ShouldRefreshIdOnlyLegacyEventsAndRequireConfirmedIdentifier(
        string? state, string? identifier, bool refreshFails, bool shouldPrune)
    {
        await using var harness = await TestHarness.CreateAsync(BuildWorkflowDefinition(1),
            new FakeTrackerClient([], state is null ? null : new Dictionary<string, string> { ["legacy-node-id"] = state },
                refreshFails: refreshFails, identifiersById: new Dictionary<string, string> { ["legacy-node-id"] = identifier! }),
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        harness.DbContext.EventLog.Add(new EventLogEntity
        {
            IssueId = "legacy-node-id", EventName = "legacy", Message = "Only retained reference", Level = "Information"
        });
        await harness.DbContext.SaveChangesAsync();

        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Contains(harness.Tracker.RefreshRequests, ids => ids.Contains("legacy-node-id"));
        Assert.Equal(shouldPrune ? 0 : 1, await harness.DbContext.EventLog.CountAsync(row => row.IssueId == "legacy-node-id"));
        if (shouldPrune)
        {
            Assert.Equal("#42", Assert.Single(harness.WorkspaceManager.CleanupRequests).IssueIdentifier);
            var refreshCount = harness.Tracker.RefreshRequests.Count;
            await harness.Service.RunTickAsync(CancellationToken.None);
            Assert.Equal(refreshCount, harness.Tracker.RefreshRequests.Count);
        }
        else
        {
            Assert.Empty(harness.WorkspaceManager.CleanupRequests);
        }
    }

    [Theory]
    [Trait("Spec", "17.4")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveWorkerLease_ShouldProtectForeignRunsRegardlessOfPersistedStatus(bool retrying)
    {
        await using var harness = await TestHarness.CreateAsync(BuildWorkflowDefinition(1),
            new FakeTrackerClient([BuildIssue("issue-1", "#1", "Closed", null)],
                new Dictionary<string, string> { ["issue-1"] = "Closed" }),
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-2");
        var run = await harness.DbContext.Runs.SingleAsync();
        var attempt = await harness.DbContext.RunAttempts.SingleAsync();
        if (retrying) run.Status = RunStatusNames.Retrying;
        await harness.DbContext.SaveChangesAsync();
        await harness.InsertLeaseAsync(WorkerLeaseNames.ForAttempt(run.Id, attempt.Id), "instance-2", DateTimeOffset.UtcNow.AddMinutes(1));

        await harness.Service.RunStartupCleanupAsync(CancellationToken.None);
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Equal("instance-2", (await harness.DbContext.Runs.SingleAsync()).OwnerInstanceId);
        Assert.Equal(retrying ? RunStatusNames.Retrying : RunStatusNames.Running, run.Status);
        Assert.Null(attempt.CompletedAtUtc);
        Assert.Empty(harness.WorkspaceManager.CleanupRequests);
        Assert.Empty(harness.Coordinator.StopRequests);
        await using var other = new SymphonyDbContext(new DbContextOptionsBuilder<SymphonyDbContext>()
            .UseSqlite(harness.DbContext.Database.GetConnectionString()).Options);
        var store = new OrchestrationCoordinationStore(other, TimeProvider.System);
        Assert.False(await store.TryClaimIssueAsync("issue-1", "#1", "poll-dispatch", "instance-1"));

        // A dead worker becomes recoverable after its attempt lease expires.
        var lease = await harness.DbContext.InstanceLeases.SingleAsync(row => row.LeaseName.StartsWith(WorkerLeaseNames.Prefix));
        lease.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        await harness.DbContext.SaveChangesAsync();
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Empty(await harness.DbContext.Runs.ToListAsync());
        Assert.Empty(await harness.DbContext.RunAttempts.ToListAsync());
        Assert.Empty(await harness.DbContext.InstanceLeases.Where(row => row.LeaseName.StartsWith(WorkerLeaseNames.Prefix)).ToListAsync());
        Assert.Single(harness.WorkspaceManager.CleanupRequests);
    }

    [Fact]
    [Trait("Spec", "17.4")]
    public async Task WorkerHeartbeat_ShouldRenewBetweenTicksObserveRemoteStopAndProtectFinalization()
    {
        var root = Directory.CreateTempSubdirectory("symphony-worker-lease-").FullName;
        try
        {
            var workflow = BuildWorkflowDefinition(1);
            workflow = workflow with { Runtime = workflow.Runtime with
            {
                Workspace = workflow.Runtime.Workspace with { Root = root },
                Hooks = workflow.Runtime.Hooks with { AfterRun = "blocked-test-hook" }
            } };
            await using var harness = await TestHarness.CreateAsync(workflow,
                new FakeTrackerClient([], new Dictionary<string, string> { ["issue-1"] = "Closed" }),
                new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
            await harness.InsertRunningRunAsync("issue-1", "#1", "Open", "instance-2");
            var run = await harness.DbContext.Runs.SingleAsync();
            var attempt = await harness.DbContext.RunAttempts.SingleAsync();
            var leaseName = WorkerLeaseNames.ForAttempt(run.Id, attempt.Id);
            var initialExpiry = DateTimeOffset.UtcNow.AddSeconds(3);
            await harness.InsertLeaseAsync(leaseName, "instance-2", initialExpiry);
            var runner = new WaitingForStopRunner();
            var hooks = new BlockingFinalizationHook();
            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
            builder.Services.AddDbContext<SymphonyDbContext>(options => options.UseSqlite(harness.DbContext.Database.GetConnectionString()));
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton(Options.Create(new ManagedLeaseOptions()));
            builder.Services.AddSingleton<ManagedLeaseRuntime>();
            builder.Services.AddScoped<IOrchestrationCoordinationStore, OrchestrationCoordinationStore>();
            builder.Services.AddSingleton<IWorkspaceManager>(new FinalizationWorkspaceManager(root, false));
            builder.Services.AddSingleton<IWorkspaceHookRunner>(hooks);
            builder.Services.AddSingleton<IWorkflowPromptRenderer, WorkflowPromptRenderer>();
            builder.Services.AddSingleton<IAgentRunner>(runner);
            builder.Services.AddSingleton<IssueExecutionCoordinator>();
            using var host = builder.Build();
            var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = new IssueExecutionRequest(run.Id, attempt.Id, "instance-2", null,
                BuildIssue("issue-1", "#1", "Open", null), workflow, TimeSpan.FromSeconds(3), committed.Task);
            var coordinator = host.Services.GetRequiredService<IssueExecutionCoordinator>();
            Assert.True(await coordinator.TryStartAsync(request));
            try
            {
                Assert.False(runner.Started.Task.IsCompleted);
                committed.SetResult();
                await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await WaitForRenewalAsync(harness.DbContext, leaseName, initialExpiry);
                // No poll lease exists for instance-2. Instance-1 must request a stop,
                // not recover this worker or delete its workspace/history.
                await harness.Service.RunTickAsync(CancellationToken.None);
                await hooks.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Empty(harness.WorkspaceManager.CleanupRequests);
                Assert.Empty(harness.Coordinator.StopRequests);
                Assert.Equal("instance-2", (await harness.DbContext.Runs.AsNoTracking().SingleAsync()).OwnerInstanceId);
                Assert.Equal(RunStopReasons.Terminal, (await harness.DbContext.Runs.AsNoTracking().SingleAsync()).RequestedStopReason);
                var before = (await harness.DbContext.InstanceLeases.AsNoTracking().SingleAsync(row => row.LeaseName == leaseName)).ExpiresAtUtc;
                await WaitForRenewalAsync(harness.DbContext, leaseName, before);
                await harness.Service.RunTickAsync(CancellationToken.None);
                Assert.Empty(harness.WorkspaceManager.CleanupRequests);
            }
            finally
            {
                hooks.Release.TrySetResult();
                committed.TrySetResult();
                await coordinator.TryStopAsync("issue-1");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (await harness.DbContext.InstanceLeases.AsNoTracking().AnyAsync(row => row.LeaseName == leaseName, timeout.Token))
                    await Task.Delay(50, timeout.Token);
            }
            Assert.Empty(await harness.DbContext.InstanceLeases.AsNoTracking().Where(row => row.LeaseName == leaseName).ToListAsync());
            Assert.NotNull((await harness.DbContext.RunAttempts.AsNoTracking().SingleAsync()).CompletedAtUtc);
            harness.DbContext.ChangeTracker.Clear();
            await harness.Service.RunTickAsync(CancellationToken.None);
            Assert.Empty(await harness.DbContext.Runs.ToListAsync());
            Assert.Single(harness.WorkspaceManager.CleanupRequests);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitForRenewalAsync(SymphonyDbContext db, string leaseName, DateTimeOffset previousExpiry)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await db.InstanceLeases.AsNoTracking().SingleAsync(row => row.LeaseName == leaseName, timeout.Token)).ExpiresAtUtc <= previousExpiry)
            await Task.Delay(50, timeout.Token);
    }

    private sealed class WaitingForStopRunner : IAgentRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AgentRunResult> RunIssueAsync(AgentRunRequest request,
            Func<AgentRunUpdate, CancellationToken, Task>? onUpdate = null, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Expected remote stop cancellation.");
        }
    }

    private sealed class BlockingFinalizationHook : IWorkspaceHookRunner
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task RunHookAsync(WorkspaceHookRequest request, CancellationToken cancellationToken = default)
        {
            if (request.HookName != "after_run") return;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }
}
