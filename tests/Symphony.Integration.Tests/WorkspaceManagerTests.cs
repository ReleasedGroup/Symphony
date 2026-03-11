using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;
using Symphony.Infrastructure.Workspaces;

namespace Symphony.Integration.Tests;

public sealed class WorkspaceManagerTests
{
    [Fact]
    public async Task PrepareIssueWorkspaceAsync_ShouldCreateSharedCloneAndWorktree()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"symphony-ws-{Guid.NewGuid():N}");
        var remoteBare = Path.Combine(tempRoot, "remote.git");
        var seedClone = Path.Combine(tempRoot, "seed");
        var workspaceRoot = Path.Combine(tempRoot, "workspaces");
        var sharedClonePath = Path.Combine(workspaceRoot, "repo");
        var worktreesRoot = Path.Combine(workspaceRoot, "worktrees");

        try
        {
            Directory.CreateDirectory(tempRoot);

            await RunGitAsync(tempRoot, ["init", "--bare", remoteBare]);
            await RunGitAsync(tempRoot, ["clone", remoteBare, seedClone]);
            await RunGitAsync(seedClone, ["config", "user.name", "symfony-tests"]);
            await RunGitAsync(seedClone, ["config", "user.email", "symphony-tests@example.com"]);
            await File.WriteAllTextAsync(Path.Combine(seedClone, "README.md"), "seed");
            await RunGitAsync(seedClone, ["add", "README.md"]);
            await RunGitAsync(seedClone, ["commit", "-m", "seed"]);
            await RunGitAsync(seedClone, ["branch", "-M", "main"]);
            await RunGitAsync(seedClone, ["push", "-u", "origin", "main"]);

            var manager = new GitWorktreeWorkspaceManager(
                new NoOpWorkspaceHookRunner(),
                NullLogger<GitWorktreeWorkspaceManager>.Instance);
            var request = new WorkspacePreparationRequest(
                IssueId: "I1",
                IssueIdentifier: "#101",
                SuggestedBranchName: null,
                WorkspaceRoot: workspaceRoot,
                SharedClonePath: sharedClonePath,
                WorktreesRoot: worktreesRoot,
                BaseBranch: "main",
                RemoteRepositoryUrl: remoteBare);

            var first = await manager.PrepareIssueWorkspaceAsync(request);
            Assert.True(first.CreatedNow);
            Assert.True(Directory.Exists(sharedClonePath));
            Assert.True(Directory.Exists(first.WorkspacePath));
            Assert.Equal("symphony/101", first.BranchName);

            var currentBranch = await RunGitAsync(first.WorkspacePath, ["rev-parse", "--abbrev-ref", "HEAD"]);
            Assert.Equal("symphony/101", currentBranch.Stdout.Trim());

            var second = await manager.PrepareIssueWorkspaceAsync(request);
            Assert.False(second.CreatedNow);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task CleanupIssueWorkspaceAsync_ShouldRunBeforeRemoveAndDeleteWorktree()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"symphony-ws-{Guid.NewGuid():N}");
        var remoteBare = Path.Combine(tempRoot, "remote.git");
        var seedClone = Path.Combine(tempRoot, "seed");
        var workspaceRoot = Path.Combine(tempRoot, "workspaces");
        var sharedClonePath = Path.Combine(workspaceRoot, "repo");
        var worktreesRoot = Path.Combine(workspaceRoot, "worktrees");

        try
        {
            Directory.CreateDirectory(tempRoot);

            await RunGitAsync(tempRoot, ["init", "--bare", remoteBare]);
            await RunGitAsync(tempRoot, ["clone", remoteBare, seedClone]);
            await RunGitAsync(seedClone, ["config", "user.name", "symfony-tests"]);
            await RunGitAsync(seedClone, ["config", "user.email", "symphony-tests@example.com"]);
            await File.WriteAllTextAsync(Path.Combine(seedClone, "README.md"), "seed");
            await RunGitAsync(seedClone, ["add", "README.md"]);
            await RunGitAsync(seedClone, ["commit", "-m", "seed"]);
            await RunGitAsync(seedClone, ["branch", "-M", "main"]);
            await RunGitAsync(seedClone, ["push", "-u", "origin", "main"]);

            var hookRunner = new RecordingWorkspaceHookRunner();
            var manager = new GitWorktreeWorkspaceManager(
                hookRunner,
                NullLogger<GitWorktreeWorkspaceManager>.Instance);

            var prepare = await manager.PrepareIssueWorkspaceAsync(new WorkspacePreparationRequest(
                IssueId: "I1",
                IssueIdentifier: "#101",
                SuggestedBranchName: null,
                WorkspaceRoot: workspaceRoot,
                SharedClonePath: sharedClonePath,
                WorktreesRoot: worktreesRoot,
                BaseBranch: "main",
                RemoteRepositoryUrl: remoteBare));

            var cleanup = await manager.CleanupIssueWorkspaceAsync(new WorkspaceCleanupRequest(
                IssueIdentifier: "#101",
                WorkspaceRoot: workspaceRoot,
                SharedClonePath: sharedClonePath,
                WorktreesRoot: worktreesRoot,
                BeforeRemoveHook: "echo cleanup",
                HookTimeoutMs: 10_000));

            Assert.True(cleanup.Existed);
            Assert.True(cleanup.RemovedNow);
            Assert.False(Directory.Exists(prepare.WorkspacePath));
            Assert.Single(hookRunner.Requests);
            Assert.Equal("before_remove", hookRunner.Requests[0].HookName);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task PrepareIssueWorkspaceAsync_ShouldFailWhenWorktreePathCollidesWithFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"symphony-ws-{Guid.NewGuid():N}");
        var remoteBare = Path.Combine(tempRoot, "remote.git");
        var seedClone = Path.Combine(tempRoot, "seed");
        var workspaceRoot = Path.Combine(tempRoot, "workspaces");
        var sharedClonePath = Path.Combine(workspaceRoot, "repo");
        var worktreesRoot = Path.Combine(workspaceRoot, "worktrees");
        var collidingPath = Path.Combine(worktreesRoot, "issue_101");

        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(worktreesRoot);
            await File.WriteAllTextAsync(collidingPath, "collision");

            await RunGitAsync(tempRoot, ["init", "--bare", remoteBare]);
            await RunGitAsync(tempRoot, ["clone", remoteBare, seedClone]);
            await RunGitAsync(seedClone, ["config", "user.name", "symphony-tests"]);
            await RunGitAsync(seedClone, ["config", "user.email", "symphony-tests@example.com"]);
            await File.WriteAllTextAsync(Path.Combine(seedClone, "README.md"), "seed");
            await RunGitAsync(seedClone, ["add", "README.md"]);
            await RunGitAsync(seedClone, ["commit", "-m", "seed"]);
            await RunGitAsync(seedClone, ["branch", "-M", "main"]);
            await RunGitAsync(seedClone, ["push", "-u", "origin", "main"]);

            var manager = new GitWorktreeWorkspaceManager(
                new NoOpWorkspaceHookRunner(),
                NullLogger<GitWorktreeWorkspaceManager>.Instance);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.PrepareIssueWorkspaceAsync(new WorkspacePreparationRequest(
                    IssueId: "I1",
                    IssueIdentifier: "issue 101",
                    SuggestedBranchName: null,
                    WorkspaceRoot: workspaceRoot,
                    SharedClonePath: sharedClonePath,
                    WorktreesRoot: worktreesRoot,
                    BaseBranch: "main",
                    RemoteRepositoryUrl: remoteBare)));

            Assert.Contains("already exists as a file", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task<GitCommandResult> RunGitAsync(string workingDirectory, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = info };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start git process.");
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git failed (exit {process.ExitCode}) with args '{string.Join(' ', args)}'{Environment.NewLine}{stderr}");
        }

        return new GitCommandResult(stdout, stderr);
    }

    private sealed record GitCommandResult(string Stdout, string Stderr);

    private sealed class NoOpWorkspaceHookRunner : IWorkspaceHookRunner
    {
        public Task RunHookAsync(WorkspaceHookRequest request, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }

    private sealed class RecordingWorkspaceHookRunner : IWorkspaceHookRunner
    {
        public List<WorkspaceHookRequest> Requests { get; } = [];

        public Task RunHookAsync(WorkspaceHookRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }
}
