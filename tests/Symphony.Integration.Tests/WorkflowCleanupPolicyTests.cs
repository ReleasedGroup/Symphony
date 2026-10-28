using Microsoft.EntityFrameworkCore;
using Symphony.Infrastructure.Workflows;

namespace Symphony.Integration.Tests;

public sealed class WorkflowCleanupPolicyTests
{
    [Theory]
    [InlineData("", "ineligible", false)]
    [InlineData("workspace:\n  cleanup:\n    mode: retain", "retain", false)]
    [InlineData("workspace:\n  cleanup:\n    mode: TERMINAL\n    dry_run: true", "terminal", true)]
    public void Loader_ReadsCleanupPolicy(string settings, string mode, bool dryRun)
    {
        var workflow = new WorkflowLoader().ParseContent(
            "---\ntracker:\n  kind: github\n  api_key: $GITHUB_TOKEN\n  owner: example\n  repo: example\n" + settings + "\n---\nPrompt", "test.md");
        Assert.Equal(mode, workflow.Runtime.Workspace.CleanupMode);
        Assert.Equal(dryRun, workflow.Runtime.Workspace.CleanupDryRun);
    }

    [Theory]
    [InlineData("mode: force")]
    [InlineData("mode: ''")]
    [InlineData("dry_run: maybe")]
    public void Loader_RejectsInvalidCleanupPolicy(string setting)
    {
        Assert.Throws<Symphony.Infrastructure.Workflows.Models.WorkflowLoadException>(() => new WorkflowLoader().ParseContent(
            "---\ntracker:\n  kind: github\n  api_key: $GITHUB_TOKEN\n  owner: example\n  repo: example\nworkspace:\n  cleanup:\n    " + setting + "\n---\nPrompt", "test.md"));
    }
}

public sealed partial class OrchestrationTickServiceTests
{
    [Theory]
    [Trait("Spec", "8.7")]
    [InlineData("retain", false)]
    [InlineData("ineligible", true)]
    public async Task CleanupPolicy_ReloadBeforeRemovalPreservesWorkspace(string mode, bool dryRun)
    {
        var workflow = BuildWorkflowDefinition(1);
        var provider = new ReloadableCleanupWorkflowProvider(workflow);
        var tracker = new FakeTrackerClient([], new Dictionary<string, string> { ["issue-1"] = "Closed" });
        tracker.BeforeStateRefresh = count =>
        {
            if (count == 2)
                provider.Current = workflow with { Runtime = workflow.Runtime with
                {
                    Workspace = workflow.Runtime.Workspace with { CleanupMode = mode, CleanupDryRun = dryRun }
                } };
        };
        await using var harness = await TestHarness.CreateAsync(workflow, tracker,
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning), workflowProvider: provider);
        await harness.InsertIssueCacheAsync("issue-1", "#1", "Closed");
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Empty(harness.WorkspaceManager.CleanupRequests);
        Assert.Single(await harness.DbContext.IssueCache.ToListAsync());
        Assert.Empty(await harness.DbContext.DispatchClaims.Where(row => row.Status == "cleanup").ToListAsync());
    }

    private sealed class ReloadableCleanupWorkflowProvider(
        Symphony.Infrastructure.Workflows.Models.WorkflowDefinition workflow) : IWorkflowDefinitionProvider
    {
        public Symphony.Infrastructure.Workflows.Models.WorkflowDefinition Current { get; set; } = workflow;
        public Task<Symphony.Infrastructure.Workflows.Models.WorkflowDefinition> GetCurrentAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Current);
    }

    [Theory]
    [Trait("Spec", "8.7")]
    [InlineData("retain", false, "Closed", false)]
    [InlineData("terminal", false, "Closed", true)]
    [InlineData("terminal", false, "Open", false)]
    [InlineData("ineligible", false, "Open", true)]
    [InlineData("ineligible", true, "Closed", false)]
    [InlineData("terminal", true, "Closed", false)]
    public async Task CleanupPolicy_GatesRemovalAndHistory(string mode, bool dryRun, string state, bool removed)
    {
        var workflow = BuildWorkflowDefinition(1);
        workflow = workflow with { Runtime = workflow.Runtime with
        {
            Workspace = workflow.Runtime.Workspace with { CleanupMode = mode, CleanupDryRun = dryRun }
        } };
        await using var harness = await TestHarness.CreateAsync(workflow,
            new FakeTrackerClient([], new Dictionary<string, string> { ["issue-1"] = state }, matchesCandidateFilters: false),
            new FakeIssueExecutionCoordinator(FakeDispatchOutcome.LeaveRunning));
        await harness.InsertIssueCacheAsync("issue-1", "#1", state);
        await harness.InsertRetryingRunAsync("issue-1", "#1", state, "instance-1", totalTokens: 150);
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Equal(removed, harness.WorkspaceManager.CleanupRequests.Count > 0);
        Assert.Equal(!removed, await harness.DbContext.IssueCache.AnyAsync());
        Assert.Equal(removed ? 150 : 0, (await harness.DbContext.RuntimeTotals.AsNoTracking().SingleAsync()).TotalTokens);
        Assert.Empty(await harness.DbContext.DispatchClaims.Where(row => row.Status == "cleanup").ToListAsync());
        await harness.Service.RunTickAsync(CancellationToken.None);
        Assert.Equal(removed ? 150 : 0, (await harness.DbContext.RuntimeTotals.AsNoTracking().SingleAsync()).TotalTokens);
    }
}
