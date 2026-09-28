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
    public async Task WorkflowEditorService_ShouldMaskAndPreserveInlineTrackerApiKeyOnSave()
    {
        var workflowPath = CreateWorkflowPath();
        await File.WriteAllTextAsync(workflowPath, """
            ---
            tracker:
              kind: github
              endpoint: https://api.github.com/graphql
              api_key: inline-secret-token
              owner: released
              repo: symphony
            polling:
              interval_ms: 600000
            ---
            Prompt A
            """);

        try
        {
            var editorService = CreateEditorService(workflowPath);
            var document = await editorService.GetCurrentAsync();

            Assert.True(document.HasMaskedTrackerApiKey);
            Assert.Contains(WorkflowEditorService.TrackerApiKeyPlaceholder, document.FrontMatterText, StringComparison.Ordinal);

            var updated = document with
            {
                FrontMatterText = document.FrontMatterText.Replace("owner: released", "owner: updated-owner", StringComparison.Ordinal),
                PromptTemplate = "Prompt B updated"
            };

            var saved = await editorService.SaveAsync(updated);
            var rawContent = await File.ReadAllTextAsync(workflowPath);

            Assert.Contains("api_key: inline-secret-token", rawContent, StringComparison.Ordinal);
            Assert.DoesNotContain(WorkflowEditorService.TrackerApiKeyPlaceholder, rawContent, StringComparison.Ordinal);
            Assert.Contains("owner: updated-owner", rawContent, StringComparison.Ordinal);
            Assert.Contains("Prompt B updated", rawContent, StringComparison.Ordinal);
            Assert.Equal("Prompt B updated", saved.PromptTemplate);
        }
        finally
        {
            File.Delete(workflowPath);
        }
    }

    [Fact]
    public async Task WorkflowEditorService_ShouldSurfaceValidationErrorsForBrokenWorkflowText()
    {
        var workflowPath = CreateWorkflowPath();
        await File.WriteAllTextAsync(workflowPath, """
            ---
            tracker:
              kind: github
              owner: released
            """);

        try
        {
            var editorService = CreateEditorService(workflowPath);
            var document = await editorService.GetCurrentAsync();

            Assert.NotNull(document.ValidationError);
            Assert.Contains("tracker:", document.FrontMatterText, StringComparison.Ordinal);
            Assert.Equal(string.Empty, document.PromptTemplate);
        }
        finally
        {
            File.Delete(workflowPath);
        }
    }

    [Fact]
    public async Task WorkflowEditorService_ShouldRejectSavingTrackerPlaceholderWithoutInlineSecretToRestore()
    {
        var workflowPath = CreateWorkflowPath();
        await File.WriteAllTextAsync(workflowPath, """
            ---
            tracker:
              kind: github
              endpoint: https://api.github.com/graphql
              api_key: $GITHUB_TOKEN
              owner: released
              repo: symphony
            ---
            Prompt body.
            """);

        try
        {
            var editorService = CreateEditorService(workflowPath);
            var document = await editorService.GetCurrentAsync();

            var updated = document with
            {
                FrontMatterText = document.FrontMatterText.Replace(
                    "$GITHUB_TOKEN",
                    WorkflowEditorService.TrackerApiKeyPlaceholder,
                    StringComparison.Ordinal)
            };

            var ex = await Assert.ThrowsAsync<WorkflowLoadException>(() => editorService.SaveAsync(updated));
            Assert.Equal(WorkflowEditorService.InvalidTrackerApiKeyPlaceholderCode, ex.Code);
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

    [Fact]
    public async Task WorkflowEditorService_ShouldRejectStaleConcurrentSave()
    {
        var path = CreateWorkflowPath();
        await File.WriteAllTextAsync(path, "---\ntracker:\n  kind: github\n  api_key: secret-value\n  owner: released\n  repo: symphony\n---\nPrompt A\n");
        try
        {
            var editorA = CreateEditorService(path);
            var editorB = CreateEditorService(path);
            var first = await editorA.GetCurrentAsync();
            var second = await editorB.GetCurrentAsync();
            var saved = await editorA.SaveAsync(first with { PromptTemplate = "Prompt B" });

            Assert.NotEqual(first.ContentRevision, saved.ContentRevision);
            var conflict = await Assert.ThrowsAsync<WorkflowEditorConflictException>(() =>
                editorB.SaveAsync(second with { PromptTemplate = "Prompt C" }));
            Assert.Equal(second.ContentRevision, conflict.ExpectedRevision);
            Assert.Equal(saved.ContentRevision, conflict.CurrentRevision);
            Assert.Contains("Prompt B", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".edit.lock");
        }
    }

    [Fact]
    public async Task WorkflowEditorService_ShouldValidateWithoutWritingAndMaskSecrets()
    {
        var path = CreateWorkflowPath();
        const string secret = "inline-secret-never-return";
        var original = "---\ntracker:\n  kind: github\n  api_key: " + secret + "\n  owner: released\n  repo: symphony\ncustom_secret: hidden-value\n---\nPrompt A\n";
        await File.WriteAllTextAsync(path, original);
        try
        {
            var editor = CreateEditorService(path);
            var document = await editor.GetCurrentAsync();
            Assert.DoesNotContain(secret, document.FrontMatterText, StringComparison.Ordinal);
            Assert.DoesNotContain("hidden-value", document.FrontMatterText, StringComparison.Ordinal);
            var invalid = document with { PromptTemplate = "{{ if }}" };
            var validation = await editor.ValidateAsync(invalid);
            Assert.False(validation.Valid);
            Assert.NotNull(validation.Error);
            Assert.DoesNotContain(secret, validation.Error!.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<WorkflowLoadException>(() => editor.SaveAsync(invalid));
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".edit.lock");
        }
    }

    [Fact]
    public async Task WorkflowEditorService_ShouldShowLastGoodRevisionAfterInvalidReload()
    {
        var path = CreateWorkflowPath();
        await File.WriteAllTextAsync(path, "---\ntracker:\n  kind: github\n  api_key: original-secret\n  owner: released\n  repo: symphony\n---\nPrompt A\n");
        using var provider = CreateProvider(path);
        try
        {
            var original = await provider.GetCurrentAsync();
            await File.WriteAllTextAsync(path, "---\ntracker: [\napi_key: exposed-secret\n---\nPrompt B\n");
            var editor = new WorkflowEditorService(new WorkflowLoader(),
                Options.Create(new WorkflowLoaderOptions { Path = path }), provider);
            var document = await editor.GetCurrentAsync();
            Assert.Equal(original.ContentRevision, document.EffectiveLoadedRevision);
            Assert.NotEqual(document.ContentRevision, document.EffectiveLoadedRevision);
            Assert.NotNull(document.ValidationError);
            Assert.DoesNotContain("exposed-secret", document.FrontMatterText, StringComparison.Ordinal);
            Assert.DoesNotContain("exposed-secret", document.ValidationError!.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WorkflowEditorService_ShouldMaskQuotedYamlKeysAndShortPromptSecrets()
    {
        var path = CreateWorkflowPath();
        await File.WriteAllTextAsync(path, "---\ntracker:\n  kind: github\n  \"api_key\": \"abc\"\n  owner: released\n  repo: symphony\n'custom_secret': xyz\n---\ntoken: q\n");
        try
        {
            var editor = CreateEditorService(path);
            var document = await editor.GetCurrentAsync();
            Assert.True(document.HasMaskedTrackerApiKey);
            Assert.DoesNotContain("abc", document.FrontMatterText, StringComparison.Ordinal);
            Assert.DoesNotContain("xyz", document.FrontMatterText, StringComparison.Ordinal);
            Assert.DoesNotContain("token: q", document.PromptTemplate, StringComparison.Ordinal);
            Assert.Contains(WorkflowEditorService.TrackerApiKeyPlaceholder, document.FrontMatterText, StringComparison.Ordinal);
            var saved = await editor.SaveAsync(document with { PromptTemplate = document.PromptTemplate + "\nAnother line." });
            Assert.NotNull(saved.ContentRevision);
            var persisted = await File.ReadAllTextAsync(path);
            Assert.Contains("\"api_key\": \"abc\"", persisted, StringComparison.Ordinal);
            Assert.Contains("token: q", persisted, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".edit.lock");
        }
    }

    [Fact]
    public async Task WorkflowEditorService_ShouldRejectPlaceholderMovedToPromptOrOtherYamlKey()
    {
        var path = CreateWorkflowPath();
        await File.WriteAllTextAsync(path, "---\ntracker:\n  kind: github\n  api_key: inline-secret\n  owner: released\n  repo: symphony\n---\nPrompt\n");
        try
        {
            var editor = CreateEditorService(path);
            var document = await editor.GetCurrentAsync();
            var movedToPrompt = document with { PromptTemplate = "Prompt\n" + WorkflowEditorService.TrackerApiKeyPlaceholder };
            var promptError = await Assert.ThrowsAsync<WorkflowLoadException>(() => editor.SaveAsync(movedToPrompt));
            Assert.Equal("invalid_workflow_editor_secret_placeholder", promptError.Code);

            var movedToOtherKey = document with
            {
                FrontMatterText = document.FrontMatterText.Replace(
                    "api_key: " + WorkflowEditorService.TrackerApiKeyPlaceholder,
                    "other_key: " + WorkflowEditorService.TrackerApiKeyPlaceholder,
                    StringComparison.Ordinal)
            };
            var yamlError = await Assert.ThrowsAsync<WorkflowLoadException>(() => editor.SaveAsync(movedToOtherKey));
            Assert.Equal("invalid_workflow_editor_secret_placeholder", yamlError.Code);

            var movedUnderOtherParent = document with
            {
                FrontMatterText = document.FrontMatterText.Replace(
                    "  api_key: " + WorkflowEditorService.TrackerApiKeyPlaceholder,
                    string.Empty, StringComparison.Ordinal) +
                    "\nother:\n  api_key: " + WorkflowEditorService.TrackerApiKeyPlaceholder
            };
            var pathError = await Assert.ThrowsAsync<WorkflowLoadException>(() => editor.SaveAsync(movedUnderOtherParent));
            Assert.Equal("invalid_workflow_editor_secret_placeholder", pathError.Code);
            Assert.Contains("api_key: inline-secret", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".edit.lock");
        }
    }

    [Fact]
    public async Task WorkflowEditorService_ShouldFailWhenLockDirectoryDisappears()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"symphony-workflow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "WORKFLOW.md");
        await File.WriteAllTextAsync(path, "---\ntracker:\n  kind: github\n  api_key: $GITHUB_TOKEN\n  owner: released\n  repo: symphony\n---\nPrompt\n");
        var editor = CreateEditorService(path);
        var document = await editor.GetCurrentAsync();
        Directory.Delete(directory, recursive: true);
        var error = await Assert.ThrowsAsync<WorkflowLoadException>(() => editor.SaveAsync(document));
        Assert.Equal("missing_workflow_file", error.Code);
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

    private static WorkflowEditorService CreateEditorService(string workflowPath)
    {
        return new WorkflowEditorService(
            new WorkflowLoader(),
            Options.Create(new WorkflowLoaderOptions { Path = workflowPath }));
    }
}
