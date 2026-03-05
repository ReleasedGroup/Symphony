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

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunIssueAsync(CreateRequest("id-0", "#0", workspace.FullName, "", 30_000)));

        Assert.Equal("Command", ex.ParamName);
    }

    [Fact]
    public async Task RunIssueAsync_ShouldExecuteCommandInWorkspace()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-runner"));
        var runner = new CodexAgentRunner(NullLogger<CodexAgentRunner>.Instance);

        var result = await runner.RunIssueAsync(CreateRequest("id-1", "#1", workspace.FullName, EchoCommand(), 30_000));

        if (!result.Success)
        {
            throw new Xunit.Sdk.XunitException(
                $"Run failed. ExitCode={result.ExitCode}{Environment.NewLine}StdOut={result.Stdout}{Environment.NewLine}StdErr={result.Stderr}");
        }
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("symphony-runner-ok", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunIssueAsync_ShouldFailWhenCommandTimesOut()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-runner"));
        var runner = new CodexAgentRunner(NullLogger<CodexAgentRunner>.Instance);

        var result = await runner.RunIssueAsync(CreateRequest("id-2", "#2", workspace.FullName, SleepCommand(), 200));

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

        var result = await runner.RunIssueAsync(CreateRequest("id-3", "#3", workspace.FullName, LargeOutputCommand(), 30_000));

        Assert.True(result.Success);
        Assert.Contains("truncated", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunIssueAsync_ShouldExecuteAppServerHandshakeForAppServerCommand()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-runner"));
        var scriptPath = Path.Combine(workspace.FullName, "fake-app-server.ps1");
        await File.WriteAllTextAsync(scriptPath, FakeAppServerScript());

        var runner = new CodexAgentRunner(NullLogger<CodexAgentRunner>.Instance);
        var command = $"powershell -NoProfile -ExecutionPolicy Bypass -File {scriptPath}";

        var result = await runner.RunIssueAsync(CreateRequest("id-4", "#4", workspace.FullName, command, 30_000));

        if (!result.Success)
        {
            throw new Xunit.Sdk.XunitException(
                $"App-server run failed. ExitCode={result.ExitCode}{Environment.NewLine}StdOut={result.Stdout}{Environment.NewLine}StdErr={result.Stderr}");
        }
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("initialize", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("thread/start", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("turn/start", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    private static AgentRunRequest CreateRequest(
        string issueId,
        string issueIdentifier,
        string workspacePath,
        string command,
        int timeoutMs)
    {
        return new AgentRunRequest(
            IssueId: issueId,
            IssueIdentifier: issueIdentifier,
            WorkspacePath: workspacePath,
            Prompt: "test prompt",
            Command: command,
            TimeoutMs: timeoutMs,
            ApprovalPolicy: "never",
            ThreadSandbox: "danger-full-access",
            TurnSandboxPolicy: "danger-full-access",
            ReadTimeoutMs: 5_000);
    }

    private static string EchoCommand()
    {
        return "echo symphony-runner-ok";
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

    private static string FakeAppServerScript()
    {
        return """
            $seen = New-Object System.Collections.Generic.List[string]
            while (($line = [Console]::In.ReadLine()) -ne $null) {
                if ([string]::IsNullOrWhiteSpace($line)) {
                    continue
                }

                $request = $line | ConvertFrom-Json
                $seen.Add($request.method)

                if ($request.method -eq 'initialize') {
                    @{ id = $request.id; result = @{ serverInfo = @{ name = 'fake'; version = '1.0' } } } | ConvertTo-Json -Compress
                    continue
                }

                if ($request.method -eq 'thread/start') {
                    @{ id = $request.id; result = @{ thread = @{ id = 'thread-1' } } } | ConvertTo-Json -Compress
                    continue
                }

                if ($request.method -eq 'turn/start') {
                    @{ id = $request.id; result = @{ turn = @{ id = 'turn-1' } } } | ConvertTo-Json -Compress
                    continue
                }

                if ($request.method -eq 'shutdown') {
                    @{ id = $request.id; result = @{ seen = $seen } } | ConvertTo-Json -Compress
                    break
                }
            }
            """;
    }
}
