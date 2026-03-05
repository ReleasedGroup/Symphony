using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Core.Models;
using Symphony.Infrastructure.Agent.Codex;

namespace Symphony.Integration.Tests;

public sealed class CodexAgentRunnerTests
{
    [Fact]
    public async Task RunIssueAsync_ShouldUsePropertyParameterNamesForValidationErrors()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-runner"));
        var runner = new CodexAgentRunner(NullLogger<CodexAgentRunner>.Instance);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => runner.RunIssueAsync(
            new AgentRunRequest(
                IssueId: "id-0",
                IssueIdentifier: "#0",
                WorkspacePath: workspace.FullName,
                Prompt: "test prompt",
                Command: "",
                TimeoutMs: 30_000)));

        Assert.Equal("Command", ex.ParamName);
    }

    [Fact]
    public async Task RunIssueAsync_ShouldExecuteCommandInWorkspace()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-runner"));
        var runner = new CodexAgentRunner(NullLogger<CodexAgentRunner>.Instance);

        var result = await runner.RunIssueAsync(
            new AgentRunRequest(
                IssueId: "id-1",
                IssueIdentifier: "#1",
                WorkspacePath: workspace.FullName,
                Prompt: "test prompt",
                Command: EchoCommand(),
                TimeoutMs: 30_000));

        Assert.True(result.Success);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("symphony-runner-ok", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunIssueAsync_ShouldFailWhenCommandTimesOut()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-runner"));
        var runner = new CodexAgentRunner(NullLogger<CodexAgentRunner>.Instance);

        var result = await runner.RunIssueAsync(
            new AgentRunRequest(
                IssueId: "id-2",
                IssueIdentifier: "#2",
                WorkspacePath: workspace.FullName,
                Prompt: "test prompt",
                Command: SleepCommand(),
                TimeoutMs: 200));

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("timed out", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunIssueAsync_ShouldCapCapturedOutput()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-runner"));
        var runner = new CodexAgentRunner(NullLogger<CodexAgentRunner>.Instance);

        var result = await runner.RunIssueAsync(
            new AgentRunRequest(
                IssueId: "id-3",
                IssueIdentifier: "#3",
                WorkspacePath: workspace.FullName,
                Prompt: "test prompt",
                Command: LargeOutputCommand(),
                TimeoutMs: 30_000));

        Assert.True(result.Success);
        Assert.Contains("truncated", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    private static string EchoCommand()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "echo symphony-runner-ok"
            : "echo symphony-runner-ok";
    }

    private static string SleepCommand()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "powershell -NoProfile -Command \"Start-Sleep -Seconds 5\""
            : "sleep 5";
    }

    private static string LargeOutputCommand()
    {
        return "for /L %i in (1,1,40000) do @echo 0123456789";
    }
}
