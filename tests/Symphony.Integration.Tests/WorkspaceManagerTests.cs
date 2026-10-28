using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;
using Symphony.Infrastructure.Workspaces;

namespace Symphony.Integration.Tests;

public sealed class WorkspaceManagerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Spec", "17.3")]
    public async Task Cleanup_ShouldPreserveNewOrModifiedWorkAfterPartialRemoval(bool addedFile)
    {
        var root = Directory.CreateTempSubdirectory("symphony-changed-remnant-").FullName;
        var tree = Directory.CreateDirectory(Path.Combine(root, "trees", "1")).FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tree, "old.txt"), "original");
            await WriteCleanupManifestAsync(tree);
            var changed = Path.Combine(tree, addedFile ? "new.txt" : "old.txt");
            await File.WriteAllTextAsync(changed, "user work");
            var hooks = new RecordingWorkspaceHookRunner();
            var manager = new GitWorktreeWorkspaceManager(hooks, NullLogger<GitWorktreeWorkspaceManager>.Instance);
            var result = await manager.CleanupIssueWorkspaceAsync(new WorkspaceCleanupRequest("#1", root,
                Path.Combine(root, "repo"), Path.Combine(root, "trees"), "echo cleanup", 1000));
            Assert.False(result.RemovedNow);
            Assert.Empty(hooks.Requests);
            Assert.Equal("user work", await File.ReadAllTextAsync(changed));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    [Trait("Spec", "17.3")]
    public async Task Cleanup_ShouldNotRunHookForInvalidOwnershipMarker()
    {
        var root = Directory.CreateTempSubdirectory("symphony-marker-").FullName;
        var tree = Directory.CreateDirectory(Path.Combine(root, "trees", "1")).FullName;
        try
        {
            await File.WriteAllTextAsync(tree + ".symphony-cleanup", "different workspace");
            await File.WriteAllTextAsync(Path.Combine(tree, "keep.txt"), "keep");
            var hooks = new RecordingWorkspaceHookRunner();
            var manager = new GitWorktreeWorkspaceManager(hooks, NullLogger<GitWorktreeWorkspaceManager>.Instance);
            var result = await manager.CleanupIssueWorkspaceAsync(new WorkspaceCleanupRequest("#1", root,
                Path.Combine(root, "repo"), Path.Combine(root, "trees"), "echo cleanup", 1000));
            Assert.False(result.RemovedNow);
            Assert.Empty(hooks.Requests);
            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(tree, "keep.txt")));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    [Trait("Spec", "17.3")]
    public async Task Cleanup_ShouldPreserveUnknownDirectory()
    {
        var root = Directory.CreateTempSubdirectory("symphony-unknown-").FullName;
        var tree = Directory.CreateDirectory(Path.Combine(root, "trees", "1")).FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tree, "user.txt"), "keep");
            var manager = new GitWorktreeWorkspaceManager(new NoOpWorkspaceHookRunner(), NullLogger<GitWorktreeWorkspaceManager>.Instance);
            var result = await manager.CleanupIssueWorkspaceAsync(new WorkspaceCleanupRequest("#1", root,
                Path.Combine(root, "repo"), Path.Combine(root, "trees"), null, 1000));
            Assert.False(result.RemovedNow);
            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(tree, "user.txt")));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    [Trait("Spec", "17.3")]
    public async Task Cleanup_ShouldRejectJunctionWithoutTouchingTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("symphony-junction-").FullName;
        var outside = Directory.CreateTempSubdirectory("symphony-outside-").FullName;
        var trees = Directory.CreateDirectory(Path.Combine(root, "trees")).FullName;
        var link = Path.Combine(trees, "1");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(outside, "keep.txt"), "keep");
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{outside}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            var manager = new GitWorktreeWorkspaceManager(new NoOpWorkspaceHookRunner(), NullLogger<GitWorktreeWorkspaceManager>.Instance);
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CleanupIssueWorkspaceAsync(
                new WorkspaceCleanupRequest("#1", root, Path.Combine(root, "repo"), trees, null, 1000)));
            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(outside, "keep.txt")));
        }
        finally
        {
            // Remove only the fixture junction itself, never recursively follow it.
            if (Directory.Exists(link)) Directory.Delete(link);
            TryDeleteDirectory(root);
            TryDeleteDirectory(outside);
        }
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [Trait("Spec", "17.3")]
    public async Task Cleanup_ShouldRejectParentIdentifiers(string identifier)
    {
        var root = Directory.CreateTempSubdirectory("symphony-safety-").FullName;
        try
        {
            var manager = new GitWorktreeWorkspaceManager(new NoOpWorkspaceHookRunner(), NullLogger<GitWorktreeWorkspaceManager>.Instance);
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CleanupIssueWorkspaceAsync(
                new WorkspaceCleanupRequest(identifier, root, Path.Combine(root, "repo"), Path.Combine(root, "trees"), null, 1000)));
            Assert.True(Directory.Exists(root));
        }
        finally { Directory.Delete(root); }
    }

    [Fact]
    [Trait("Spec", "17.3")]
    public async Task Cleanup_ShouldRetryWindowsLockAndRecoverAfterRelease()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("symphony-lock-").FullName;
        var tree = Directory.CreateDirectory(Path.Combine(root, "trees", "1")).FullName;
        try
        {
            // Model a verified worktree partially removed by a previous cleanup attempt.
            var file = Path.Combine(tree, "locked.txt");
            await File.WriteAllTextAsync(file, "fixture");
            await WriteCleanupManifestAsync(tree);
            using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var manager = new GitWorktreeWorkspaceManager(new NoOpWorkspaceHookRunner(), NullLogger<GitWorktreeWorkspaceManager>.Instance);
            var cleanup = manager.CleanupIssueWorkspaceAsync(new WorkspaceCleanupRequest("#1", root,
                Path.Combine(root, "repo"), Path.Combine(root, "trees"), null, 1000));
            await Task.Delay(350);
            held.Dispose();
            Assert.True((await cleanup).RemovedNow);
            Assert.False(Directory.Exists(tree));
        }
        finally { TryDeleteDirectory(root); }
    }

    [Fact]
    [Trait("Spec", "17.3")]
    public async Task Cleanup_ShouldRemainQueuedWhenWindowsLockPersists()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("symphony-lock-").FullName;
        var tree = Directory.CreateDirectory(Path.Combine(root, "trees", "1")).FullName;
        try
        {
            var file = Path.Combine(tree, "locked.txt");
            await File.WriteAllTextAsync(file, "fixture");
            await WriteCleanupManifestAsync(tree);
            var manager = new GitWorktreeWorkspaceManager(new NoOpWorkspaceHookRunner(), NullLogger<GitWorktreeWorkspaceManager>.Instance);
            var request = new WorkspaceCleanupRequest("#1", root, Path.Combine(root, "repo"), Path.Combine(root, "trees"), null, 1000);
            using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.False((await manager.CleanupIssueWorkspaceAsync(request)).RemovedNow);
            Assert.True((await manager.CleanupIssueWorkspaceAsync(request)).RemovedNow);
        }
        finally { TryDeleteDirectory(root); }
    }

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

            var upstreamRemote = await RunGitAsync(
                sharedClonePath,
                ["config", "--get", "branch.symphony/101.remote"],
                throwOnNonZeroExitCode: false);
            Assert.Equal(1, upstreamRemote.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(upstreamRemote.Stdout));

            var second = await manager.PrepareIssueWorkspaceAsync(request);
            Assert.False(second.CreatedNow);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task PrepareIssueWorkspaceAsync_ShouldWaitForSharedCloneMutationLock()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"symphony-ws-{Guid.NewGuid():N}");
        var remoteBare = Path.Combine(tempRoot, "remote.git");
        var seedClone = Path.Combine(tempRoot, "seed");
        var workspaceRoot = Path.Combine(tempRoot, "workspaces");
        var sharedClonePath = Path.Combine(workspaceRoot, "repo");
        var worktreesRoot = Path.Combine(workspaceRoot, "worktrees");
        var lockPath = $"{sharedClonePath}.lock";

        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(workspaceRoot);

            await RunGitAsync(tempRoot, ["init", "--bare", remoteBare]);
            await RunGitAsync(tempRoot, ["clone", remoteBare, seedClone]);
            await RunGitAsync(seedClone, ["config", "user.name", "symfony-tests"]);
            await RunGitAsync(seedClone, ["config", "user.email", "symphony-tests@example.com"]);
            await File.WriteAllTextAsync(Path.Combine(seedClone, "README.md"), "seed");
            await RunGitAsync(seedClone, ["add", "README.md"]);
            await RunGitAsync(seedClone, ["commit", "-m", "seed"]);
            await RunGitAsync(seedClone, ["branch", "-M", "main"]);
            await RunGitAsync(seedClone, ["push", "-u", "origin", "main"]);

            using var heldLock = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            var manager = new GitWorktreeWorkspaceManager(
                new NoOpWorkspaceHookRunner(),
                NullLogger<GitWorktreeWorkspaceManager>.Instance);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

            var prepareTask = manager.PrepareIssueWorkspaceAsync(
                new WorkspacePreparationRequest(
                    IssueId: "I1",
                    IssueIdentifier: "#101",
                    SuggestedBranchName: null,
                    WorkspaceRoot: workspaceRoot,
                    SharedClonePath: sharedClonePath,
                    WorktreesRoot: worktreesRoot,
                    BaseBranch: "main",
                    RemoteRepositoryUrl: remoteBare),
                cts.Token);

            var firstCompleted = await Task.WhenAny(prepareTask, Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token));
            Assert.NotSame(prepareTask, firstCompleted);

            heldLock.Dispose();

            var result = await prepareTask.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
            Assert.True(result.CreatedNow);
            Assert.True(Directory.Exists(result.WorkspacePath));
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

            var uncommittedFile = Path.Combine(prepare.WorkspacePath, "keep.txt");
            await File.WriteAllTextAsync(uncommittedFile, "user work");
            var preserved = await manager.CleanupIssueWorkspaceAsync(new WorkspaceCleanupRequest(
                "#101", workspaceRoot, sharedClonePath, worktreesRoot, "echo cleanup", 1000));
            Assert.False(preserved.RemovedNow);
            Assert.Empty(hookRunner.Requests);
            Assert.Equal("user work", await File.ReadAllTextAsync(uncommittedFile));
            File.Delete(uncommittedFile);

            await RunGitAsync(prepare.WorkspacePath, ["checkout", "--detach"]);
            var detached = await manager.CleanupIssueWorkspaceAsync(new WorkspaceCleanupRequest(
                "#101", workspaceRoot, sharedClonePath, worktreesRoot, "echo cleanup", 1000));
            Assert.False(detached.RemovedNow);
            Assert.Empty(hookRunner.Requests);
            await RunGitAsync(prepare.WorkspacePath, ["checkout", prepare.BranchName]);

            // A local-only committed change survives cleanup in the retained branch.
            await RunGitAsync(prepare.WorkspacePath, ["config", "user.name", "symphony-tests"]);
            await RunGitAsync(prepare.WorkspacePath, ["config", "user.email", "symphony-tests@example.com"]);
            await File.WriteAllTextAsync(Path.Combine(prepare.WorkspacePath, "unpublished.txt"), "local work");
            await RunGitAsync(prepare.WorkspacePath, ["add", "unpublished.txt"]);
            await RunGitAsync(prepare.WorkspacePath, ["commit", "-m", "unpublished work"]);

            var copied = Directory.CreateDirectory(Path.Combine(worktreesRoot, "102")).FullName;
            File.Copy(Path.Combine(prepare.WorkspacePath, ".git"), Path.Combine(copied, ".git"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CleanupIssueWorkspaceAsync(
                new WorkspaceCleanupRequest("#102", workspaceRoot, sharedClonePath, worktreesRoot, "echo cleanup", 1000)));
            Assert.Empty(hookRunner.Requests);
            Assert.True(Directory.Exists(prepare.WorkspacePath));
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
            var retained = await RunGitAsync(sharedClonePath, ["show", $"{prepare.BranchName}:unpublished.txt"]);
            Assert.Equal("local work", retained.Stdout.Trim());
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

    private static async Task WriteCleanupManifestAsync(string tree)
    {
        var files = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories))
            files.Add(Path.GetRelativePath(tree, file), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(file))));
        await File.WriteAllTextAsync(tree + ".symphony-cleanup", System.Text.Json.JsonSerializer.Serialize(new { WorkspacePath = tree, Files = files }));
    }

    private static async Task<GitCommandResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        bool throwOnNonZeroExitCode = true)
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

        if (throwOnNonZeroExitCode && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git failed (exit {process.ExitCode}) with args '{string.Join(' ', args)}'{Environment.NewLine}{stderr}");
        }

        return new GitCommandResult(process.ExitCode, stdout, stderr);
    }

    private sealed record GitCommandResult(int ExitCode, string Stdout, string Stderr);

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
