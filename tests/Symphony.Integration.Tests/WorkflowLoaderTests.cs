using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Symphony.Core.Configuration;
using Symphony.Infrastructure.Workflows;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Integration.Tests;

public sealed class WorkflowLoaderTests
{
    [Fact]
    public async Task LoadAsync_ShouldParseFrontMatterAndPrompt()
    {
        var workflowPath = CreateWorkflowPath();
        await File.WriteAllTextAsync(workflowPath, """
            ---
            tracker:
              kind: github
              endpoint: https://api.github.com/graphql
              api_key: test-token
              owner: released
              repo: symphony
              labels: [backend]
              active_states: Open, In Progress
            polling:
              interval_ms: 120000
            agent:
              max_concurrent_agents: 3
            ---
            Test prompt body.
            """);

        try
        {
            var loader = new WorkflowLoader();
            var definition = await loader.LoadAsync(workflowPath);

            Assert.Equal("github", definition.Runtime.Tracker.Kind);
            Assert.Equal("released", definition.Runtime.Tracker.Owner);
            Assert.Equal("symphony", definition.Runtime.Tracker.Repo);
            Assert.True(definition.Runtime.Tracker.IncludePullRequests);
            Assert.Equal(120000, definition.Runtime.Polling.IntervalMs);
            Assert.Equal(3, definition.Runtime.Agent.MaxConcurrentAgents);
            Assert.Equal(20, definition.Runtime.Agent.MaxTurns);
            Assert.Equal(300_000, definition.Runtime.Agent.MaxRetryBackoffMs);
            Assert.Empty(definition.Runtime.Agent.MaxConcurrentAgentsByState);
            Assert.Null(definition.Runtime.Server.Port);
            Assert.Equal("./workspaces", definition.Runtime.Workspace.Root);
            Assert.Equal("./workspaces/repo", definition.Runtime.Workspace.SharedClonePath);
            Assert.Equal("./workspaces/worktrees", definition.Runtime.Workspace.WorktreesRoot);
            Assert.Equal("main", definition.Runtime.Workspace.BaseBranch);
            Assert.Null(definition.Runtime.Hooks.AfterCreate);
            Assert.Null(definition.Runtime.Hooks.BeforeRun);
            Assert.Null(definition.Runtime.Hooks.AfterRun);
            Assert.Null(definition.Runtime.Hooks.BeforeRemove);
            Assert.Equal(60_000, definition.Runtime.Hooks.TimeoutMs);
            Assert.Equal("codex app-server", definition.Runtime.Codex.Command);
            Assert.Equal(3_600_000, definition.Runtime.Codex.TurnTimeoutMs);
            Assert.Equal("never", definition.Runtime.Codex.ApprovalPolicy);
            Assert.Equal("danger-full-access", definition.Runtime.Codex.ThreadSandbox);
            Assert.Equal("danger-full-access", definition.Runtime.Codex.TurnSandboxPolicy);
            Assert.Equal(5_000, definition.Runtime.Codex.ReadTimeoutMs);
            Assert.Equal(300_000, definition.Runtime.Codex.StallTimeoutMs);
            Assert.Equal("Test prompt body.", definition.PromptTemplate);
        }
        finally
        {
            File.Delete(workflowPath);
        }
    }

    [Fact]
    public async Task Provider_ShouldReloadWhenWorkflowChanges()
    {
        var workflowPath = CreateWorkflowPath();
        await File.WriteAllTextAsync(workflowPath, """
            ---
            tracker:
              kind: github
              endpoint: https://api.github.com/graphql
              api_key: test-token
              owner: owner-a
              repo: repo-a
            ---
            Prompt A
            """);

        try
        {
            var provider = CreateProvider(workflowPath);

            var first = await provider.GetCurrentAsync();
            Assert.Equal("owner-a", first.Runtime.Tracker.Owner);

            await File.WriteAllTextAsync(workflowPath, """
                ---
                tracker:
                  kind: github
                  endpoint: https://api.github.com/graphql
                  api_key: test-token
                  owner: owner-bb
                  repo: repo-bb
                ---
                Prompt B updated
                """);

            var second = await provider.GetCurrentAsync();
            Assert.Equal("owner-bb", second.Runtime.Tracker.Owner);
            Assert.Equal("Prompt B updated", second.PromptTemplate);
        }
        finally
        {
            File.Delete(workflowPath);
        }
    }

    [Fact]
    public async Task Provider_ShouldKeepLastKnownGoodWhenReloadFails()
    {
        var workflowPath = CreateWorkflowPath();
        await File.WriteAllTextAsync(workflowPath, """
            ---
            tracker:
              kind: github
              endpoint: https://api.github.com/graphql
              api_key: test-token
              owner: owner-a
              repo: repo-a
            ---
            Prompt A
            """);

        try
        {
            var provider = CreateProvider(workflowPath);

            var first = await provider.GetCurrentAsync();
            Assert.Equal("owner-a", first.Runtime.Tracker.Owner);

            await File.WriteAllTextAsync(workflowPath, """
                ---
                tracker:
                  kind: github
                  owner: owner-b
                """);

            var second = await provider.GetCurrentAsync();
            Assert.Equal("owner-a", second.Runtime.Tracker.Owner);
        }
        finally
        {
            File.Delete(workflowPath);
        }
    }

    [Fact]
    public async Task Provider_ShouldFailWhenConfiguredWorkflowPathEnvironmentVariableIsMissing()
    {
        var missingPathEnvVar = $"SYMPHONY_WORKFLOW_PATH_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(missingPathEnvVar, null);

        try
        {
            var provider = CreateProvider($"${missingPathEnvVar}");
            var ex = await Assert.ThrowsAsync<WorkflowLoadException>(() => provider.GetCurrentAsync());
            Assert.Equal("missing_workflow_file", ex.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable(missingPathEnvVar, null);
        }
    }

    [Fact]
    public async Task LoadAsync_ShouldParseExtendedWorkflowSettings()
    {
        var workflowPath = CreateWorkflowPath();
        var workspaceRootEnvVar = $"SYMPHONY_WORKFLOW_ROOT_{Guid.NewGuid():N}";
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"workflow-root-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable(workspaceRootEnvVar, workspaceRoot);

        try
        {
            await File.WriteAllTextAsync(workflowPath, $$"""
                ---
                tracker:
                  kind: github
                  endpoint: https://api.github.com/graphql
                  api_key: $GITHUB_TOKEN
                  owner: released
                  repo: symphony
                  include_pull_requests: false
                agent:
                  max_concurrent_agents: 4
                  max_turns: 7
                  max_retry_backoff_ms: "90000"
                  max_concurrent_agents_by_state:
                    Open: 2
                    " In Progress ": "4"
                    Closed: 0
                    Invalid: nope
                workspace:
                  root: ${{workspaceRootEnvVar}}
                  shared_clone_path: ~/symphony-shared
                  worktrees_root: .\worktrees
                codex:
                  command: codex app-server --profile "$HOME/test"
                  turn_timeout_ms: 120000
                  stall_timeout_ms: 0
                ---
                Prompt body.
                """);

            var loader = new WorkflowLoader();
            var definition = await loader.LoadAsync(workflowPath);

            Assert.False(definition.Runtime.Tracker.IncludePullRequests);
            Assert.Equal(7, definition.Runtime.Agent.MaxTurns);
            Assert.Equal(90_000, definition.Runtime.Agent.MaxRetryBackoffMs);
            Assert.Equal(2, definition.Runtime.Agent.MaxConcurrentAgentsByState["open"]);
            Assert.Equal(4, definition.Runtime.Agent.MaxConcurrentAgentsByState["in progress"]);
            Assert.DoesNotContain("closed", definition.Runtime.Agent.MaxConcurrentAgentsByState.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.Null(definition.Runtime.Server.Port);
            Assert.Equal(workspaceRoot, definition.Runtime.Workspace.Root);
            Assert.Equal(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "symphony-shared"),
                definition.Runtime.Workspace.SharedClonePath);
            Assert.Equal(@".\worktrees", definition.Runtime.Workspace.WorktreesRoot);
            Assert.Equal("codex app-server --profile \"$HOME/test\"", definition.Runtime.Codex.Command);
            Assert.Equal(120_000, definition.Runtime.Codex.TurnTimeoutMs);
            Assert.Equal(0, definition.Runtime.Codex.StallTimeoutMs);
        }
        finally
        {
            Environment.SetEnvironmentVariable(workspaceRootEnvVar, null);
            File.Delete(workflowPath);
        }
    }

    [Fact]
    public async Task LoadAsync_ShouldParseCodexSettings()
    {
        var workflowPath = CreateWorkflowPath();
        await File.WriteAllTextAsync(workflowPath, """
            ---
            tracker:
              kind: github
              api_key: test-token
              owner: released
              repo: symphony
            codex:
              command: codex app-server --verbose
              turn_timeout_ms: 120000
              approval_policy: never
              thread_sandbox: workspace-write
              turn_sandbox_policy: workspace-write
              read_timeout_ms: 9000
              stall_timeout_ms: 180000
            ---
            Prompt body.
            """);

        try
        {
            var loader = new WorkflowLoader();
            var definition = await loader.LoadAsync(workflowPath);

            Assert.Equal("codex app-server --verbose", definition.Runtime.Codex.Command);
            Assert.Equal(120000, definition.Runtime.Codex.TurnTimeoutMs);
            Assert.Equal("never", definition.Runtime.Codex.ApprovalPolicy);
            Assert.Equal("workspace-write", definition.Runtime.Codex.ThreadSandbox);
            Assert.Equal("workspace-write", definition.Runtime.Codex.TurnSandboxPolicy);
            Assert.Equal(9000, definition.Runtime.Codex.ReadTimeoutMs);
            Assert.Equal(180000, definition.Runtime.Codex.StallTimeoutMs);
        }
        finally
        {
            File.Delete(workflowPath);
        }
    }

    [Fact]
    public async Task LoadAsync_ShouldParseHookSettings()
    {
        var workflowPath = CreateWorkflowPath();
        await File.WriteAllTextAsync(workflowPath, """
            ---
            tracker:
              kind: github
              api_key: test-token
              owner: released
              repo: symphony
            hooks:
              after_create: |
                echo setup
              before_run: |
                echo before
              after_run: |
                echo after
              before_remove: |
                echo cleanup
              timeout_ms: 120000
            ---
            Prompt body.
            """);

        try
        {
            var loader = new WorkflowLoader();
            var definition = await loader.LoadAsync(workflowPath);

            Assert.Equal("echo setup\n", definition.Runtime.Hooks.AfterCreate);
            Assert.Equal("echo before\n", definition.Runtime.Hooks.BeforeRun);
            Assert.Equal("echo after\n", definition.Runtime.Hooks.AfterRun);
            Assert.Equal("echo cleanup\n", definition.Runtime.Hooks.BeforeRemove);
            Assert.Equal(120000, definition.Runtime.Hooks.TimeoutMs);
        }
        finally
        {
            File.Delete(workflowPath);
        }
    }

    [Fact]
    public async Task LoadAsync_ShouldRejectInvalidTurnTimeout()
    {
        var workflowPath = CreateWorkflowPath();
        await File.WriteAllTextAsync(workflowPath, """
            ---
            tracker:
              kind: github
              api_key: test-token
              owner: released
              repo: symphony
            codex:
              turn_timeout_ms: 0
            ---
            Prompt body.
            """);

        try
        {
            var loader = new WorkflowLoader();
            var ex = await Assert.ThrowsAsync<WorkflowLoadException>(() => loader.LoadAsync(workflowPath));
            Assert.Equal("invalid_codex_turn_timeout", ex.Code);
        }
        finally
        {
            File.Delete(workflowPath);
        }
    }

    [Fact]
    public async Task LoadAsync_ShouldParseServerPort()
    {
        var workflowPath = CreateWorkflowPath();
        await File.WriteAllTextAsync(workflowPath, """
            ---
            tracker:
              kind: github
              api_key: test-token
              owner: released
              repo: symphony
            server:
              port: 0
            ---
            Prompt body.
            """);

        try
        {
            var loader = new WorkflowLoader();
            var definition = await loader.LoadAsync(workflowPath);
            Assert.Equal(0, definition.Runtime.Server.Port);
        }
        finally
        {
            File.Delete(workflowPath);
        }
    }

    [Fact]
    public async Task LoadAsync_ShouldRejectInvalidServerPort()
    {
        var workflowPath = CreateWorkflowPath();
        await File.WriteAllTextAsync(workflowPath, """
            ---
            tracker:
              kind: github
              api_key: test-token
              owner: released
              repo: symphony
            server:
              port: -1
            ---
            Prompt body.
            """);

        try
        {
            var loader = new WorkflowLoader();
            var ex = await Assert.ThrowsAsync<WorkflowLoadException>(() => loader.LoadAsync(workflowPath));
            Assert.Equal("invalid_server_port", ex.Code);
        }
        finally
        {
            File.Delete(workflowPath);
        }
    }

    [Fact]
    public async Task LoadAsync_ShouldRejectInvalidCodexReadTimeout()
    {
        var workflowPath = CreateWorkflowPath();
        await File.WriteAllTextAsync(workflowPath, """
            ---
            tracker:
              kind: github
              api_key: test-token
              owner: released
              repo: symphony
            codex:
              read_timeout_ms: 0
            ---
            Prompt body.
            """);

        try
        {
            var loader = new WorkflowLoader();
            var ex = await Assert.ThrowsAsync<WorkflowLoadException>(() => loader.LoadAsync(workflowPath));
            Assert.Equal("invalid_codex_read_timeout", ex.Code);
        }
        finally
        {
            File.Delete(workflowPath);
        }
    }

    [Fact]
    public async Task LoadAsync_ShouldFallbackInvalidHooksTimeoutToDefault()
    {
        var workflowPath = CreateWorkflowPath();
        await File.WriteAllTextAsync(workflowPath, """
            ---
            tracker:
              kind: github
              api_key: test-token
              owner: released
              repo: symphony
            hooks:
              timeout_ms: 0
            ---
            Prompt body.
            """);

        try
        {
            var loader = new WorkflowLoader();
            var definition = await loader.LoadAsync(workflowPath);
            Assert.Equal(60_000, definition.Runtime.Hooks.TimeoutMs);
        }
        finally
        {
            File.Delete(workflowPath);
        }
    }

    private static string CreateWorkflowPath()
    {
        return Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-workflow.md");
    }

    private static WorkflowDefinitionProvider CreateProvider(string workflowPath)
    {
        return new WorkflowDefinitionProvider(
            new WorkflowLoader(),
            Options.Create(new WorkflowLoaderOptions { Path = workflowPath }),
            NullLogger<WorkflowDefinitionProvider>.Instance);
    }
}
