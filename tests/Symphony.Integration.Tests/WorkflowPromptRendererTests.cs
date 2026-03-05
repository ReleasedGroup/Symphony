using Symphony.Core.Models;
using Symphony.Infrastructure.Workflows;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Integration.Tests;

public sealed class WorkflowPromptRendererTests
{
    [Fact]
    public void RenderForIssue_ShouldRenderIssueFields()
    {
        var renderer = new WorkflowPromptRenderer();
        var definition = CreateDefinition("Issue {{ issue.identifier }}: {{ issue.title }}");

        var output = renderer.RenderForIssue(definition, CreateIssue());

        Assert.Equal("Issue #42: Fix orchestration dispatch", output);
    }

    [Fact]
    public void RenderForIssue_ShouldFailOnUnknownVariable()
    {
        var renderer = new WorkflowPromptRenderer();
        var definition = CreateDefinition("Value: {{ issue.missing_field }}");

        var ex = Assert.Throws<WorkflowLoadException>(() => renderer.RenderForIssue(definition, CreateIssue()));

        Assert.Equal("template_render_error", ex.Code);
    }

    private static WorkflowDefinition CreateDefinition(string template)
    {
        var runtime = new WorkflowRuntimeSettings(
            new WorkflowTrackerSettings(
                Kind: "github",
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "token",
                Owner: "released",
                Repo: "symphony",
                Milestone: null,
                Labels: [],
                ActiveStates: ["Open"],
                TerminalStates: ["Closed"]),
            new WorkflowPollingSettings(600000),
            new WorkflowAgentSettings(5),
            new WorkflowWorkspaceSettings(
                Root: "./workspaces",
                SharedClonePath: "./workspaces/repo",
                WorktreesRoot: "./workspaces/worktrees",
                BaseBranch: "main",
                RemoteUrl: null),
            new WorkflowCodexSettings(
                Command: "codex app-server",
                TimeoutMs: 3600000));

        return new WorkflowDefinition(
            Config: new Dictionary<string, object?>(),
            PromptTemplate: template,
            Runtime: runtime,
            SourcePath: "WORKFLOW.md",
            LoadedAtUtc: DateTimeOffset.UtcNow);
    }

    private static NormalizedIssue CreateIssue()
    {
        return new NormalizedIssue(
            Id: "issue-42",
            Identifier: "#42",
            Title: "Fix orchestration dispatch",
            Description: "Details",
            Priority: 1,
            State: "Open",
            BranchName: "issue-42",
            Url: "https://example.test/issue/42",
            Milestone: "v1",
            Labels: ["backend"],
            PullRequests: [],
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);
    }
}
