using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
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
        var workflowPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-workflow.md");
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

        var loader = new WorkflowLoader();
        var definition = await loader.LoadAsync(workflowPath);

        Assert.Equal("github", definition.Runtime.Tracker.Kind);
        Assert.Equal("released", definition.Runtime.Tracker.Owner);
        Assert.Equal("symphony", definition.Runtime.Tracker.Repo);
        Assert.Equal(120000, definition.Runtime.Polling.IntervalMs);
        Assert.Equal(3, definition.Runtime.Agent.MaxConcurrentAgents);
        Assert.Equal("Test prompt body.", definition.PromptTemplate);

        File.Delete(workflowPath);
    }

    [Fact]
    public async Task Provider_ShouldKeepLastKnownGoodWhenReloadFails()
    {
        var workflowPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-workflow.md");
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

        var provider = new WorkflowDefinitionProvider(
            new WorkflowLoader(),
            Options.Create(new WorkflowLoaderOptions { Path = workflowPath }),
            new TestHostEnvironment(Path.GetTempPath()),
            NullLogger<WorkflowDefinitionProvider>.Instance);

        var first = await provider.GetCurrentAsync();
        Assert.Equal("owner-a", first.Runtime.Tracker.Owner);

        await Task.Delay(50);
        await File.WriteAllTextAsync(workflowPath, """
            ---
            tracker:
              kind: github
              owner: owner-b
            """);

        var second = await provider.GetCurrentAsync();
        Assert.Equal("owner-a", second.Runtime.Tracker.Owner);

        File.Delete(workflowPath);
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Symphony.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
