using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symphony.Core.Abstractions;
using Symphony.Core.Configuration;
using Symphony.Core.Models;
using Symphony.Host.Services;
using Symphony.Infrastructure.Tracker.GitHub;
using Symphony.Infrastructure.Workflows;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Integration.Tests;

public sealed class OrchestrationTickServiceTests
{
    [Fact]
    public async Task RunTickAsync_ShouldDispatchByPriorityThenCreatedAtWithAgentLimit()
    {
        var workflow = BuildWorkflowDefinition(maxConcurrentAgents: 2);
        var tracker = new FakeTrackerClient(
        [
            BuildIssue("issue-1", "#1", priority: 2, createdAt: new DateTimeOffset(2026, 01, 03, 0, 0, 0, TimeSpan.Zero)),
            BuildIssue("issue-2", "#2", priority: 1, createdAt: new DateTimeOffset(2026, 01, 04, 0, 0, 0, TimeSpan.Zero)),
            BuildIssue("issue-3", "#3", priority: 1, createdAt: new DateTimeOffset(2026, 01, 02, 0, 0, 0, TimeSpan.Zero)),
            BuildIssue("issue-4", "#4", priority: null, createdAt: new DateTimeOffset(2026, 01, 01, 0, 0, 0, TimeSpan.Zero))
        ]);
        var coordinationStore = new FakeCoordinationStore(leaseGranted: true);
        var workspaceManager = new FakeWorkspaceManager();
        var hookRunner = new FakeWorkspaceHookRunner();
        var agentRunner = new FakeAgentRunner();

        var service = CreateService(
            workflow,
            tracker,
            coordinationStore,
            workspaceManager,
            hookRunner,
            agentRunner);

        var interval = await service.RunTickAsync(CancellationToken.None);

        Assert.Equal(workflow.Runtime.Polling.IntervalMs, interval);
        Assert.Equal(["issue-3", "issue-2"], agentRunner.RunIssueIds);
        Assert.Equal(2, coordinationStore.ReleaseCalls.Count);
        Assert.All(coordinationStore.ReleaseCalls, call => Assert.Equal("agent_succeeded", call.ReleaseStatus));
    }

    [Fact]
    public async Task RunTickAsync_ShouldContinuePastUnclaimableIssuesUntilAgentLimit()
    {
        var workflow = BuildWorkflowDefinition(maxConcurrentAgents: 2);
        var tracker = new FakeTrackerClient(
        [
            BuildIssue("issue-1", "#1", priority: 2, createdAt: new DateTimeOffset(2026, 01, 03, 0, 0, 0, TimeSpan.Zero)),
            BuildIssue("issue-2", "#2", priority: 1, createdAt: new DateTimeOffset(2026, 01, 04, 0, 0, 0, TimeSpan.Zero)),
            BuildIssue("issue-3", "#3", priority: 1, createdAt: new DateTimeOffset(2026, 01, 02, 0, 0, 0, TimeSpan.Zero)),
            BuildIssue("issue-4", "#4", priority: null, createdAt: new DateTimeOffset(2026, 01, 01, 0, 0, 0, TimeSpan.Zero))
        ]);
        var coordinationStore = new FakeCoordinationStore(
            leaseGranted: true,
            unclaimableIssueIds: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "issue-3" });
        var workspaceManager = new FakeWorkspaceManager();
        var hookRunner = new FakeWorkspaceHookRunner();
        var agentRunner = new FakeAgentRunner();

        var service = CreateService(
            workflow,
            tracker,
            coordinationStore,
            workspaceManager,
            hookRunner,
            agentRunner);

        var interval = await service.RunTickAsync(CancellationToken.None);

        Assert.Equal(workflow.Runtime.Polling.IntervalMs, interval);
        Assert.Equal(["issue-3", "issue-2", "issue-1"], coordinationStore.ClaimAttempts);
        Assert.Equal(["issue-2", "issue-1"], agentRunner.RunIssueIds);
        Assert.Equal(2, coordinationStore.ReleaseCalls.Count);
        Assert.All(coordinationStore.ReleaseCalls, call => Assert.Equal("agent_succeeded", call.ReleaseStatus));
    }

    [Fact]
    public async Task RunTickAsync_ShouldSkipDispatchWhenLeaseNotOwned()
    {
        var workflow = BuildWorkflowDefinition(maxConcurrentAgents: 5);
        var tracker = new FakeTrackerClient(
        [
            BuildIssue("issue-1", "#1", priority: 1, createdAt: DateTimeOffset.UtcNow)
        ]);
        var coordinationStore = new FakeCoordinationStore(leaseGranted: false);
        var workspaceManager = new FakeWorkspaceManager();
        var hookRunner = new FakeWorkspaceHookRunner();
        var agentRunner = new FakeAgentRunner();

        var service = CreateService(
            workflow,
            tracker,
            coordinationStore,
            workspaceManager,
            hookRunner,
            agentRunner);

        var interval = await service.RunTickAsync(CancellationToken.None);

        Assert.Equal(workflow.Runtime.Polling.IntervalMs, interval);
        Assert.Empty(agentRunner.RunIssueIds);
        Assert.Empty(coordinationStore.ReleaseCalls);
    }

    [Fact]
    public async Task RunTickAsync_ShouldRunAfterCreateBeforeRunAndAfterRunHooks()
    {
        var workflow = BuildWorkflowDefinition(
            maxConcurrentAgents: 1,
            hooks: new WorkflowHooksSettings(
                AfterCreate: "echo after-create",
                BeforeRun: "echo before-run",
                AfterRun: "echo after-run",
                BeforeRemove: null,
                TimeoutMs: 60_000));
        var tracker = new FakeTrackerClient(
        [
            BuildIssue("issue-1", "#1", priority: 1, createdAt: DateTimeOffset.UtcNow)
        ]);
        var coordinationStore = new FakeCoordinationStore(leaseGranted: true);
        var workspaceManager = new FakeWorkspaceManager(createdNow: true);
        var hookRunner = new FakeWorkspaceHookRunner();
        var agentRunner = new FakeAgentRunner();

        var service = CreateService(
            workflow,
            tracker,
            coordinationStore,
            workspaceManager,
            hookRunner,
            agentRunner);

        var interval = await service.RunTickAsync(CancellationToken.None);

        Assert.Equal(workflow.Runtime.Polling.IntervalMs, interval);
        Assert.Equal(["after_create", "before_run", "after_run"], hookRunner.ExecutedHooks);
        Assert.Equal(["issue-1"], agentRunner.RunIssueIds);
        Assert.Equal("agent_succeeded", coordinationStore.ReleaseCalls.Single().ReleaseStatus);
    }

    [Fact]
    public async Task RunTickAsync_ShouldFailAttemptWhenBeforeRunHookFails()
    {
        var workflow = BuildWorkflowDefinition(
            maxConcurrentAgents: 1,
            hooks: new WorkflowHooksSettings(
                AfterCreate: null,
                BeforeRun: "echo before-run",
                AfterRun: "echo after-run",
                BeforeRemove: null,
                TimeoutMs: 60_000));
        var tracker = new FakeTrackerClient(
        [
            BuildIssue("issue-1", "#1", priority: 1, createdAt: DateTimeOffset.UtcNow)
        ]);
        var coordinationStore = new FakeCoordinationStore(leaseGranted: true);
        var workspaceManager = new FakeWorkspaceManager(createdNow: true);
        var hookRunner = new FakeWorkspaceHookRunner(
            failingHooks: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "before_run" });
        var agentRunner = new FakeAgentRunner();

        var service = CreateService(
            workflow,
            tracker,
            coordinationStore,
            workspaceManager,
            hookRunner,
            agentRunner);

        var interval = await service.RunTickAsync(CancellationToken.None);

        Assert.Equal(workflow.Runtime.Polling.IntervalMs, interval);
        Assert.Equal(["before_run"], hookRunner.ExecutedHooks);
        Assert.Empty(agentRunner.RunIssueIds);
        Assert.Equal("before_run_failed", coordinationStore.ReleaseCalls.Single().ReleaseStatus);
    }

    [Fact]
    public async Task RunTickAsync_ShouldIgnoreAfterRunHookFailure()
    {
        var workflow = BuildWorkflowDefinition(
            maxConcurrentAgents: 1,
            hooks: new WorkflowHooksSettings(
                AfterCreate: null,
                BeforeRun: "echo before-run",
                AfterRun: "echo after-run",
                BeforeRemove: null,
                TimeoutMs: 60_000));
        var tracker = new FakeTrackerClient(
        [
            BuildIssue("issue-1", "#1", priority: 1, createdAt: DateTimeOffset.UtcNow)
        ]);
        var coordinationStore = new FakeCoordinationStore(leaseGranted: true);
        var workspaceManager = new FakeWorkspaceManager(createdNow: true);
        var hookRunner = new FakeWorkspaceHookRunner(
            failingHooks: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "after_run" });
        var agentRunner = new FakeAgentRunner();

        var service = CreateService(
            workflow,
            tracker,
            coordinationStore,
            workspaceManager,
            hookRunner,
            agentRunner);

        var interval = await service.RunTickAsync(CancellationToken.None);

        Assert.Equal(workflow.Runtime.Polling.IntervalMs, interval);
        Assert.Equal(["before_run", "after_run"], hookRunner.ExecutedHooks);
        Assert.Equal(["issue-1"], agentRunner.RunIssueIds);
        Assert.Equal("agent_succeeded", coordinationStore.ReleaseCalls.Single().ReleaseStatus);
    }

    [Fact]
    public async Task RunTickAsync_ShouldIgnoreUnexpectedAfterRunHookFailure()
    {
        var workflow = BuildWorkflowDefinition(
            maxConcurrentAgents: 1,
            hooks: new WorkflowHooksSettings(
                AfterCreate: null,
                BeforeRun: "echo before-run",
                AfterRun: "echo after-run",
                BeforeRemove: null,
                TimeoutMs: 60_000));
        var tracker = new FakeTrackerClient(
        [
            BuildIssue("issue-1", "#1", priority: 1, createdAt: DateTimeOffset.UtcNow)
        ]);
        var coordinationStore = new FakeCoordinationStore(leaseGranted: true);
        var workspaceManager = new FakeWorkspaceManager(createdNow: true);
        var hookRunner = new FakeWorkspaceHookRunner(
            unexpectedFailingHooks: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "after_run" });
        var agentRunner = new FakeAgentRunner();

        var service = CreateService(
            workflow,
            tracker,
            coordinationStore,
            workspaceManager,
            hookRunner,
            agentRunner);

        var interval = await service.RunTickAsync(CancellationToken.None);

        Assert.Equal(workflow.Runtime.Polling.IntervalMs, interval);
        Assert.Equal(["before_run", "after_run"], hookRunner.ExecutedHooks);
        Assert.Equal(["issue-1"], agentRunner.RunIssueIds);
        Assert.Equal("agent_succeeded", coordinationStore.ReleaseCalls.Single().ReleaseStatus);
    }

    [Fact]
    public async Task RunStartupCleanupAsync_ShouldCleanupTerminalIssueWorkspaces()
    {
        var workflow = BuildWorkflowDefinition(
            maxConcurrentAgents: 1,
            hooks: new WorkflowHooksSettings(
                AfterCreate: null,
                BeforeRun: null,
                AfterRun: null,
                BeforeRemove: "echo cleanup",
                TimeoutMs: 60_000));
        var terminalIssues = new[]
        {
            BuildIssue("issue-1", "#1", priority: null, createdAt: DateTimeOffset.UtcNow) with { State = "Closed" },
            BuildIssue("issue-2", "#2", priority: null, createdAt: DateTimeOffset.UtcNow) with { State = "Closed" }
        };
        var tracker = new FakeTrackerClient([], terminalIssues);
        var coordinationStore = new FakeCoordinationStore(leaseGranted: true);
        var workspaceManager = new FakeWorkspaceManager();
        var hookRunner = new FakeWorkspaceHookRunner();
        var agentRunner = new FakeAgentRunner();

        var service = CreateService(
            workflow,
            tracker,
            coordinationStore,
            workspaceManager,
            hookRunner,
            agentRunner);

        await service.RunStartupCleanupAsync(CancellationToken.None);

        Assert.True(tracker.FetchByStatesCalled);
        Assert.Equal(2, workspaceManager.CleanupRequests.Count);
        Assert.All(
            workspaceManager.CleanupRequests,
            request => Assert.Equal("echo cleanup", request.BeforeRemoveHook));
        Assert.Contains("poll-dispatch", coordinationStore.ReleasedLeases);
    }

    [Fact]
    public async Task RunStartupCleanupAsync_ShouldSkipWhenLeaseNotOwned()
    {
        var workflow = BuildWorkflowDefinition(maxConcurrentAgents: 1);
        var tracker = new FakeTrackerClient([], []);
        var coordinationStore = new FakeCoordinationStore(leaseGranted: false);
        var workspaceManager = new FakeWorkspaceManager();
        var hookRunner = new FakeWorkspaceHookRunner();
        var agentRunner = new FakeAgentRunner();

        var service = CreateService(
            workflow,
            tracker,
            coordinationStore,
            workspaceManager,
            hookRunner,
            agentRunner);

        await service.RunStartupCleanupAsync(CancellationToken.None);

        Assert.False(tracker.FetchByStatesCalled);
        Assert.Empty(workspaceManager.CleanupRequests);
        Assert.Empty(coordinationStore.ReleasedLeases);
    }

    private static OrchestrationTickService CreateService(
        WorkflowDefinition workflowDefinition,
        FakeTrackerClient tracker,
        FakeCoordinationStore coordinationStore,
        FakeWorkspaceManager workspaceManager,
        FakeWorkspaceHookRunner hookRunner,
        FakeAgentRunner agentRunner)
    {
        return new OrchestrationTickService(
            new FakeWorkflowDefinitionProvider(workflowDefinition),
            new FakePromptRenderer(),
            tracker,
            coordinationStore,
            workspaceManager,
            hookRunner,
            agentRunner,
            Options.Create(new OrchestrationOptions
            {
                InstanceId = "instance-1",
                LeaseName = "poll-dispatch",
                LeaseTtlSeconds = 900
            }),
            NullLogger<OrchestrationTickService>.Instance);
    }

    private static WorkflowDefinition BuildWorkflowDefinition(int maxConcurrentAgents, WorkflowHooksSettings? hooks = null)
    {
        var runtime = new WorkflowRuntimeSettings(
            new WorkflowTrackerSettings(
                Kind: "github",
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "test-token",
                Owner: "released",
                Repo: "symphony",
                Milestone: null,
                Labels: [],
                ActiveStates: ["Open"],
                TerminalStates: ["Closed"]),
            new WorkflowPollingSettings(600_000),
            new WorkflowAgentSettings(maxConcurrentAgents),
            new WorkflowWorkspaceSettings(
                Root: "./workspaces",
                SharedClonePath: "./workspaces/repo",
                WorktreesRoot: "./workspaces/worktrees",
                BaseBranch: "main",
                RemoteUrl: null),
            hooks ?? new WorkflowHooksSettings(
                AfterCreate: null,
                BeforeRun: null,
                AfterRun: null,
                BeforeRemove: null,
                TimeoutMs: 60_000),
            new WorkflowCodexSettings(
                Command: "echo ok",
                TimeoutMs: 30_000,
                ApprovalPolicy: "never",
                ThreadSandbox: "danger-full-access",
                TurnSandboxPolicy: "danger-full-access",
                ReadTimeoutMs: 5_000));

        return new WorkflowDefinition(
            Config: new Dictionary<string, object?>(),
            PromptTemplate: "Prompt body",
            Runtime: runtime,
            SourcePath: "WORKFLOW.md",
            LoadedAtUtc: DateTimeOffset.UtcNow);
    }

    private static NormalizedIssue BuildIssue(string id, string identifier, int? priority, DateTimeOffset createdAt)
    {
        return new NormalizedIssue(
            Id: id,
            Identifier: identifier,
            Title: $"Issue {identifier}",
            Description: null,
            Priority: priority,
            State: "Open",
            BranchName: null,
            Url: null,
            Milestone: null,
            Labels: [],
            PullRequests: [],
            CreatedAt: createdAt,
            UpdatedAt: createdAt);
    }

    private sealed class FakeWorkflowDefinitionProvider(WorkflowDefinition workflowDefinition) : IWorkflowDefinitionProvider
    {
        public Task<WorkflowDefinition> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(workflowDefinition);
        }
    }

    private sealed class FakePromptRenderer : IWorkflowPromptRenderer
    {
        public string RenderForIssue(WorkflowDefinition workflowDefinition, NormalizedIssue issue, int? attempt = null)
        {
            return $"Prompt for {issue.Identifier}";
        }
    }

    private sealed class FakeTrackerClient(
        IReadOnlyList<NormalizedIssue> issues,
        IReadOnlyList<NormalizedIssue>? terminalIssues = null,
        bool throwOnFetchByStates = false) : IGitHubTrackerClient
    {
        public bool FetchByStatesCalled { get; private set; }

        public Task<IReadOnlyList<NormalizedIssue>> FetchCandidateIssuesAsync(
            TrackerQuery query,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(issues);
        }

        public Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByStatesAsync(
            TrackerQuery query,
            IReadOnlyList<string> states,
            CancellationToken cancellationToken = default)
        {
            FetchByStatesCalled = true;
            if (throwOnFetchByStates)
            {
                throw new InvalidOperationException("terminal states fetch failed");
            }

            return Task.FromResult<IReadOnlyList<NormalizedIssue>>(terminalIssues ?? []);
        }
    }

    private sealed class FakeCoordinationStore(bool leaseGranted, IReadOnlySet<string>? unclaimableIssueIds = null) : IOrchestrationCoordinationStore
    {
        private readonly HashSet<string> _unclaimableIssueIds = unclaimableIssueIds is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(unclaimableIssueIds, StringComparer.OrdinalIgnoreCase);

        public List<string> ClaimAttempts { get; } = [];
        public List<string> ReleasedLeases { get; } = [];
        public List<(string IssueId, string InstanceId, string ReleaseStatus)> ReleaseCalls { get; } = [];

        public Task<bool> AcquireOrRenewLeaseAsync(
            string leaseName,
            string instanceId,
            TimeSpan leaseTtl,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(leaseGranted);
        }

        public Task ReleaseLeaseAsync(string leaseName, string instanceId, CancellationToken cancellationToken = default)
        {
            ReleasedLeases.Add(leaseName);
            return Task.CompletedTask;
        }

        public Task<bool> TryClaimIssueAsync(
            string issueId,
            string issueIdentifier,
            string instanceId,
            CancellationToken cancellationToken = default)
        {
            ClaimAttempts.Add(issueId);
            return Task.FromResult(!_unclaimableIssueIds.Contains(issueId));
        }

        public Task ReleaseIssueClaimAsync(
            string issueId,
            string instanceId,
            string releaseStatus,
            CancellationToken cancellationToken = default)
        {
            ReleaseCalls.Add((issueId, instanceId, releaseStatus));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkspaceManager(bool createdNow = true) : IWorkspaceManager
    {
        public List<WorkspaceCleanupRequest> CleanupRequests { get; } = [];

        public Task<WorkspacePreparationResult> PrepareIssueWorkspaceAsync(
            WorkspacePreparationRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WorkspacePreparationResult(
                WorkspacePath: $"C:\\tmp\\{request.IssueIdentifier}",
                BranchName: request.SuggestedBranchName ?? "symphony/test",
                CreatedNow: createdNow));
        }

        public Task<WorkspaceCleanupResult> CleanupIssueWorkspaceAsync(
            WorkspaceCleanupRequest request,
            CancellationToken cancellationToken = default)
        {
            CleanupRequests.Add(request);
            return Task.FromResult(new WorkspaceCleanupResult(
                WorkspacePath: $"C:\\tmp\\{request.IssueIdentifier}",
                Existed: true,
                RemovedNow: true));
        }
    }

    private sealed class FakeWorkspaceHookRunner(
        IReadOnlySet<string>? failingHooks = null,
        IReadOnlySet<string>? unexpectedFailingHooks = null) : IWorkspaceHookRunner
    {
        private readonly HashSet<string> _failingHooks = failingHooks is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(failingHooks, StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unexpectedFailingHooks = unexpectedFailingHooks is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(unexpectedFailingHooks, StringComparer.OrdinalIgnoreCase);

        public List<string> ExecutedHooks { get; } = [];

        public Task RunHookAsync(WorkspaceHookRequest request, CancellationToken cancellationToken = default)
        {
            ExecutedHooks.Add(request.HookName);
            if (_unexpectedFailingHooks.Contains(request.HookName))
            {
                throw new InvalidOperationException($"{request.HookName} unexpected failure");
            }

            if (_failingHooks.Contains(request.HookName))
            {
                throw new WorkspaceHookExecutionException(request.HookName, $"{request.HookName} failed");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeAgentRunner : IAgentRunner
    {
        public List<string> RunIssueIds { get; } = [];

        public Task<AgentRunResult> RunIssueAsync(
            AgentRunRequest request,
            CancellationToken cancellationToken = default)
        {
            RunIssueIds.Add(request.IssueId);
            return Task.FromResult(new AgentRunResult(
                Success: true,
                ExitCode: 0,
                Stdout: "ok",
                Stderr: string.Empty,
                Duration: TimeSpan.FromMilliseconds(100)));
        }
    }
}
