using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Symphony.Integration.Tests;

public sealed class ApiSmokeTests
{
    [Fact]
    public async Task HealthEndpoint_ShouldReturnSuccess()
    {
        var workflowPath = CreateValidWorkflowPath();

        try
        {
            await using var factory = CreateFactory(workflowPath);
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/api/v1/health");

            Assert.True(response.IsSuccessStatusCode);
        }
        finally
        {
            File.Delete(workflowPath);
        }
    }

    [Fact]
    public async Task RuntimeEndpoint_ShouldReturnConfiguredDefaults()
    {
        var workflowPath = CreateValidWorkflowPath();

        try
        {
            await using var factory = CreateFactory(workflowPath);
            using var client = factory.CreateClient();

            var response = await client.GetAsync("/api/v1/runtime");
            var content = await response.Content.ReadAsStringAsync();

            Assert.True(response.IsSuccessStatusCode);
            Assert.Contains("\"intervalMs\":600000", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"maxConcurrentAgents\":5", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"maxTurns\":20", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(workflowPath);
        }
    }

    [Fact]
    public void HostStartup_ShouldFailFastWhenWorkflowApiKeyCannotBeResolved()
    {
        var missingApiKeyEnvVar = $"SYMPHONY_MISSING_API_KEY_{Guid.NewGuid():N}";
        var workflowPath = CreateWorkflowPath($$"""
            ---
            tracker:
              kind: github
              endpoint: https://api.github.com/graphql
              api_key: ${{missingApiKeyEnvVar}}
              owner: released
              repo: symphony
            polling:
              interval_ms: 600000
            agent:
              max_concurrent_agents: 5
            codex:
              command: codex app-server
              turn_timeout_ms: 3600000
              approval_policy: never
              thread_sandbox: danger-full-access
              turn_sandbox_policy: danger-full-access
              read_timeout_ms: 5000
              stall_timeout_ms: 300000
            workspace:
              root: ./workspaces
              shared_clone_path: ./workspaces/repo
              worktrees_root: ./workspaces/worktrees
              base_branch: main
            hooks:
              timeout_ms: 60000
            ---
            Prompt body.
            """);

        try
        {
            Environment.SetEnvironmentVariable(missingApiKeyEnvVar, null);

            using var factory = CreateFactory(workflowPath);
            var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

            Assert.Contains("tracker.api_key", ex.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(workflowPath);
        }
    }

    private static string CreateValidWorkflowPath()
    {
        return CreateWorkflowPath("""
            ---
            tracker:
              kind: github
              endpoint: https://api.github.com/graphql
              api_key: test-token
              owner: released
              repo: symphony
            polling:
              interval_ms: 600000
            agent:
              max_concurrent_agents: 5
            codex:
              command: codex app-server
              turn_timeout_ms: 3600000
              approval_policy: never
              thread_sandbox: danger-full-access
              turn_sandbox_policy: danger-full-access
              read_timeout_ms: 5000
              stall_timeout_ms: 300000
            workspace:
              root: ./workspaces
              shared_clone_path: ./workspaces/repo
              worktrees_root: ./workspaces/worktrees
              base_branch: main
            hooks:
              timeout_ms: 60000
            ---
            Prompt body.
            """);
    }

    private static string CreateWorkflowPath(string content)
    {
        var workflowPath = Path.Combine(Path.GetTempPath(), $"symphony-int-workflow-{Guid.NewGuid():N}.md");
        File.WriteAllText(workflowPath, content);
        return workflowPath;
    }

    private static WebApplicationFactory<Program> CreateFactory(string workflowPath)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath};Cache=Shared;Mode=ReadWriteCreate";

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Workflow:Path"] = workflowPath,
                    ["Symphony:Tracker:Owner"] = "integration-owner",
                    ["Symphony:Tracker:Repo"] = "integration-repo",
                    ["Persistence:ConnectionString"] = connectionString
                });
            });
        });
    }
}
