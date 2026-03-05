using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Core.Models;
using Symphony.Infrastructure.Workspaces;

namespace Symphony.Integration.Tests;

public sealed class WorkspaceHookRunnerTests
{
    [Fact]
    public async Task RunHookAsync_ShouldExecuteMultilineScriptInWorkspace()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-hook-workspace"));
        var outputPath = Path.Combine(workspace.FullName, "hook-output.txt");
        var runner = new WorkspaceHookRunner(NullLogger<WorkspaceHookRunner>.Instance);

        var script = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? """
              set MSG=hook-ran
              echo %MSG%> "hook-output.txt"
              """
            : """
              msg="hook-ran"
              printf "%s" "$msg" > "hook-output.txt"
              """;

        await runner.RunHookAsync(
            new WorkspaceHookRequest(
                HookName: "before_run",
                Script: script,
                WorkspacePath: workspace.FullName,
                TimeoutMs: 5_000,
                IssueIdentifier: "#1"));

        var written = await File.ReadAllTextAsync(outputPath);
        Assert.Equal("hook-ran", written.Trim());
    }

    [Fact]
    public async Task RunHookAsync_ShouldWrapNonZeroExitAsWorkspaceHookExecutionException()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-hook-workspace"));
        var runner = new WorkspaceHookRunner(NullLogger<WorkspaceHookRunner>.Instance);
        var script = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "exit /b 7" : "exit 7";

        var ex = await Assert.ThrowsAsync<WorkspaceHookExecutionException>(() =>
            runner.RunHookAsync(
                new WorkspaceHookRequest(
                    HookName: "before_run",
                    Script: script,
                    WorkspacePath: workspace.FullName,
                    TimeoutMs: 5_000,
                    IssueIdentifier: "#2")));

        Assert.Equal("before_run", ex.HookName);
        Assert.False(ex.IsTimeout);
    }

    [Fact]
    public async Task RunHookAsync_ShouldTimeoutLongRunningScript()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-hook-workspace"));
        var runner = new WorkspaceHookRunner(NullLogger<WorkspaceHookRunner>.Instance);
        var script = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "ping 127.0.0.1 -n 6 > nul"
            : "sleep 5";

        var ex = await Assert.ThrowsAsync<WorkspaceHookExecutionException>(() =>
            runner.RunHookAsync(
                new WorkspaceHookRequest(
                    HookName: "before_run",
                    Script: script,
                    WorkspacePath: workspace.FullName,
                    TimeoutMs: 200,
                    IssueIdentifier: "#3")));

        Assert.Equal("before_run", ex.HookName);
        Assert.True(ex.IsTimeout);
    }
}
