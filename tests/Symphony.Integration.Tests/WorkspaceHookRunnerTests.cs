using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Core.Models;
using Symphony.Infrastructure.Workspaces;

namespace Symphony.Integration.Tests;

public sealed class WorkspaceHookRunnerTests
{
    [Fact]
    [Trait("Spec", "17.3")]
    public async Task Cancellation_ShouldWaitForOwnedChildToReleaseWorkspaceFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("symphony-child-lock-").FullName;
        using var cancellation = new CancellationTokenSource();
        var runner = new WorkspaceHookRunner(NullLogger<WorkspaceHookRunner>.Instance);
        var script = "$f = [IO.File]::Open('owned.lock', 'Create', 'ReadWrite', 'None'); [IO.File]::WriteAllText('ready', 'yes'); Start-Sleep -Seconds 30";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var run = runner.RunHookAsync(new WorkspaceHookRequest("before_run",
            $"powershell.exe -NoProfile -NonInteractive -EncodedCommand {encoded}", root, 15000, "#1"), cancellation.Token);
        try
        {
            for (var i = 0; i < 250 && !File.Exists(Path.Combine(root, "ready")) && !run.IsCompleted; i++)
                await Task.Delay(20);
            Assert.True(File.Exists(Path.Combine(root, "ready")), "Owned child must acquire the fixture lock.");
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            using var reopened = new FileStream(Path.Combine(root, "owned.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            cancellation.Cancel();
            try { await run; } catch (OperationCanceledException) { }
            Directory.Delete(root, recursive: true);
        }
    }

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

    [Fact]
    public async Task RunHookAsync_ShouldRemoveTemporaryHookDirectoryAfterExecution()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-hook-workspace"));
        var runner = new WorkspaceHookRunner(NullLogger<WorkspaceHookRunner>.Instance);

        await runner.RunHookAsync(
            new WorkspaceHookRequest(
                HookName: "before_run",
                Script: RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "echo ok" : "printf ok",
                WorkspacePath: workspace.FullName,
                TimeoutMs: 5_000,
                IssueIdentifier: "#4"));

        Assert.False(Directory.Exists(Path.Combine(workspace.FullName, ".symphony-hooks")));
    }
}
