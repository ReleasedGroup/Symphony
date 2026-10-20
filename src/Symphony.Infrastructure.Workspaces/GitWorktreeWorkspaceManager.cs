using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;

namespace Symphony.Infrastructure.Workspaces;

public sealed class GitWorktreeWorkspaceManager(
    IWorkspaceHookRunner workspaceHookRunner,
    ILogger<GitWorktreeWorkspaceManager> logger) : IWorkspaceManager
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SharedCloneLocks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<WorkspacePreparationResult> PrepareIssueWorkspaceAsync(
        WorkspacePreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceRoot))
        {
            throw new InvalidOperationException("workspace.root must be configured.");
        }

        if (string.IsNullOrWhiteSpace(request.RemoteRepositoryUrl))
        {
            throw new InvalidOperationException("workspace.remote_url could not be resolved.");
        }

        var rootPath = WorkspacePathSafety.GetAbsolutePath(request.WorkspaceRoot);
        var sharedClonePath = WorkspacePathSafety.GetAbsolutePath(request.SharedClonePath);
        var worktreesRootPath = WorkspacePathSafety.GetAbsolutePath(request.WorktreesRoot);
        var issueKey = WorkspacePathSafety.SanitizeIssueIdentifier(request.IssueIdentifier);
        var worktreePath = Path.Combine(worktreesRootPath, issueKey);
        var branchName = string.IsNullOrWhiteSpace(request.SuggestedBranchName)
            ? $"symphony/{issueKey}"
            : request.SuggestedBranchName.Trim();

        WorkspacePathSafety.EnsurePathIsWithinRoot(rootPath, sharedClonePath);
        WorkspacePathSafety.EnsurePathIsWithinRoot(rootPath, worktreesRootPath);
        WorkspacePathSafety.EnsurePathIsWithinRoot(worktreesRootPath, worktreePath);
        EnsureSeparatePaths(sharedClonePath, worktreesRootPath);

        EnsureDirectoryPathAvailable(rootPath, "workspace.root");
        EnsureDirectoryPathAvailable(worktreesRootPath, "workspace.worktrees_root");
        EnsureDirectoryPathAvailable(sharedClonePath, "workspace.shared_clone_path");
        EnsureDirectoryPathAvailable(worktreePath, $"workspace for issue '{request.IssueIdentifier}'");
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(worktreesRootPath);

        var sharedCloneLock = GetSharedCloneLock(sharedClonePath);
        await sharedCloneLock.WaitAsync(cancellationToken);
        try
        {
            using var mutationLock = await AcquireSharedCloneMutationLockAsync(
                rootPath,
                sharedClonePath,
                cancellationToken);

            await EnsureSharedCloneAsync(sharedClonePath, request.RemoteRepositoryUrl, cancellationToken);
            var baseBranchSnapshotRef = await EnsureLatestFromOriginAsync(
                sharedClonePath,
                request.BaseBranch,
                cancellationToken);

            if (Directory.Exists(worktreePath))
            {
                logger.LogInformation(
                    "Reusing existing workspace {WorkspacePath} for issue {IssueIdentifier}.",
                    worktreePath,
                    request.IssueIdentifier);

                return new WorkspacePreparationResult(worktreePath, branchName, CreatedNow: false);
            }

            try
            {
                var hasLocalBranch = await HasLocalBranchAsync(sharedClonePath, branchName, cancellationToken);
                if (hasLocalBranch)
                {
                    await RunGitAsync(sharedClonePath, ["worktree", "add", worktreePath, branchName], cancellationToken);
                }
                else
                {
                    await RunGitAsync(
                        sharedClonePath,
                        ["worktree", "add", "--no-track", "-b", branchName, worktreePath, baseBranchSnapshotRef],
                        cancellationToken);
                }
            }
            catch
            {
                if (Directory.Exists(worktreePath))
                {
                    _ = await RemoveWorktreePathAsync(sharedClonePath, worktreePath, CancellationToken.None);
                }

                throw;
            }
        }
        finally
        {
            sharedCloneLock.Release();
        }

        logger.LogInformation(
            "Prepared workspace {WorkspacePath} (branch {BranchName}) for issue {IssueIdentifier}.",
            worktreePath,
            branchName,
            request.IssueIdentifier);

        return new WorkspacePreparationResult(worktreePath, branchName, CreatedNow: true);
    }

    public async Task<WorkspaceCleanupResult> CleanupIssueWorkspaceAsync(
        WorkspaceCleanupRequest request,
        CancellationToken cancellationToken = default)
    {
        using var cleanupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cleanupTimeout.CancelAfter(TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(Math.Clamp(request.HookTimeoutMs, 0, 60_000)));
        cancellationToken = cleanupTimeout.Token;
        if (string.IsNullOrWhiteSpace(request.WorkspaceRoot))
        {
            throw new InvalidOperationException("workspace.root must be configured.");
        }

        if (string.IsNullOrWhiteSpace(request.WorktreesRoot))
        {
            throw new InvalidOperationException("workspace.worktrees_root must be configured.");
        }

        if (string.IsNullOrWhiteSpace(request.SharedClonePath))
        {
            throw new InvalidOperationException("workspace.shared_clone_path must be configured.");
        }

        var rootPath = WorkspacePathSafety.GetAbsolutePath(request.WorkspaceRoot);
        var sharedClonePath = WorkspacePathSafety.GetAbsolutePath(request.SharedClonePath);
        var worktreesRootPath = WorkspacePathSafety.GetAbsolutePath(request.WorktreesRoot);
        var issueKey = WorkspacePathSafety.SanitizeIssueIdentifier(request.IssueIdentifier);
        var worktreePath = Path.Combine(worktreesRootPath, issueKey);

        WorkspacePathSafety.EnsurePathIsWithinRoot(rootPath, sharedClonePath);
        WorkspacePathSafety.EnsurePathIsWithinRoot(rootPath, worktreesRootPath);
        WorkspacePathSafety.EnsurePathIsWithinRoot(worktreesRootPath, worktreePath);
        EnsureSeparatePaths(sharedClonePath, worktreesRootPath);

        if (File.Exists(worktreePath))
        {
            throw new InvalidOperationException($"Workspace path collision detected for issue '{request.IssueIdentifier}'.");
        }

        if (!Directory.Exists(worktreePath))
        {
            return new WorkspaceCleanupResult(worktreePath, Existed: false, RemovedNow: false);
        }

        ValidateRemovalTree(worktreePath);
        if (!File.Exists(Path.Combine(worktreePath, ".git")) && !File.Exists(worktreePath + ".symphony-cleanup"))
        {
            logger.LogWarning("workspace_cleanup_preserved: ownership unknown for {WorkspacePath}.", worktreePath);
            return new WorkspaceCleanupResult(worktreePath, Existed: true, RemovedNow: false);
        }

        if (!string.IsNullOrWhiteSpace(request.BeforeRemoveHook))
        {
            try
            {
                await workspaceHookRunner.RunHookAsync(
                    new WorkspaceHookRequest(
                        HookName: "before_remove",
                        Script: request.BeforeRemoveHook,
                        WorkspacePath: worktreePath,
                        TimeoutMs: request.HookTimeoutMs,
                        IssueIdentifier: request.IssueIdentifier),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "before_remove hook failed for workspace {WorkspacePath}. Cleanup will continue.",
                    worktreePath);
            }
        }

        var sharedCloneLock = GetSharedCloneLock(sharedClonePath);
        await sharedCloneLock.WaitAsync(cancellationToken);
        bool removedNow;
        try
        {
            using var mutationLock = await AcquireSharedCloneMutationLockAsync(
                rootPath,
                sharedClonePath,
                cancellationToken);

            removedNow = await RemoveWorktreePathAsync(sharedClonePath, worktreePath, cancellationToken);
        }
        finally
        {
            sharedCloneLock.Release();
        }

        if (removedNow)
        {
            logger.LogInformation(
                "Removed workspace {WorkspacePath} for issue {IssueIdentifier}.",
                worktreePath,
                request.IssueIdentifier);
        }
        else
        {
            logger.LogWarning(
                "Workspace cleanup did not remove path {WorkspacePath} for issue {IssueIdentifier}.",
                worktreePath,
                request.IssueIdentifier);
        }

        return new WorkspaceCleanupResult(
            WorkspacePath: worktreePath,
            Existed: true,
            RemovedNow: removedNow);
    }

    private async Task EnsureSharedCloneAsync(
        string sharedClonePath,
        string remoteRepositoryUrl,
        CancellationToken cancellationToken)
    {
        EnsureDirectoryPathAvailable(sharedClonePath, "workspace.shared_clone_path");

        var gitDirectoryPath = Path.Combine(sharedClonePath, ".git");
        if (Directory.Exists(gitDirectoryPath))
        {
            await EnsureRemoteUrlAsync(sharedClonePath, remoteRepositoryUrl, cancellationToken);
            return;
        }

        var parentDirectory = Path.GetDirectoryName(sharedClonePath);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        await RunGitAsync(
            workingDirectory: null,
            ["clone", "--no-checkout", remoteRepositoryUrl, sharedClonePath],
            cancellationToken);
    }

    private async Task EnsureRemoteUrlAsync(
        string sharedClonePath,
        string remoteRepositoryUrl,
        CancellationToken cancellationToken)
    {
        var currentRemote = await RunGitAsync(
            sharedClonePath,
            ["remote", "get-url", "origin"],
            cancellationToken,
            throwOnNonZeroExitCode: false);
        if (currentRemote.ExitCode == 0 &&
            currentRemote.Stdout.Trim().Equals(remoteRepositoryUrl, StringComparison.Ordinal))
        {
            return;
        }

        await RunGitAsync(sharedClonePath, ["remote", "set-url", "origin", remoteRepositoryUrl], cancellationToken);
    }

    private async Task<string> EnsureLatestFromOriginAsync(
        string sharedClonePath,
        string baseBranch,
        CancellationToken cancellationToken)
    {
        var snapshotRef = GetBaseBranchSnapshotRef(baseBranch);
        await RunGitAsync(
            sharedClonePath,
            ["fetch", "origin", $"+refs/heads/{baseBranch}:{snapshotRef}"],
            cancellationToken);
        return snapshotRef;
    }

    private async Task<IDisposable> AcquireSharedCloneMutationLockAsync(
        string rootPath,
        string sharedClonePath,
        CancellationToken cancellationToken)
    {
        var lockPath = GetSharedCloneLockPath(sharedClonePath);
        WorkspacePathSafety.EnsurePathIsWithinRoot(rootPath, lockPath);

        var lockDirectory = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrWhiteSpace(lockDirectory))
        {
            Directory.CreateDirectory(lockDirectory);
        }

        var loggedWait = false;
        var wait = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);

                WriteLockMetadata(stream, sharedClonePath);
                return stream;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                if (wait.Elapsed > TimeSpan.FromSeconds(10))
                {
                    throw new IOException($"Timed out waiting for shared clone mutation lock '{lockPath}'.");
                }
                if (!loggedWait)
                {
                    logger.LogInformation(
                        "Waiting for shared clone mutation lock {LockPath} for {SharedClonePath}.",
                        lockPath,
                        sharedClonePath);
                    loggedWait = true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
    }

    private async Task<bool> HasLocalBranchAsync(string sharedClonePath, string branchName, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(
            sharedClonePath,
            ["show-ref", "--verify", $"refs/heads/{branchName}"],
            cancellationToken,
            throwOnNonZeroExitCode: false);

        return result.ExitCode == 0;
    }

    private async Task<bool> RemoveWorktreePathAsync(
        string sharedClonePath,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        // Refuse linked descendants before invoking Git or recursive filesystem operations.
        ValidateRemovalTree(worktreePath);
        var marker = worktreePath + ".symphony-cleanup";
        WorkspacePathSafety.EnsureNoReparsePoints(marker);
        var gitFile = Path.Combine(worktreePath, ".git");
        if (File.Exists(gitFile))
        {
            var gitReference = (await File.ReadAllTextAsync(gitFile, cancellationToken)).Trim();
            if (!gitReference.StartsWith("gitdir: ", StringComparison.Ordinal))
                throw new InvalidOperationException("Workspace does not have a Git worktree reference.");
            var gitPath = Path.GetFullPath(gitReference[8..], worktreePath);
            WorkspacePathSafety.EnsurePathIsWithinRoot(Path.Combine(sharedClonePath, ".git", "worktrees"), gitPath);
            var status = await RunGitAsync(worktreePath, ["status", "--porcelain", "--untracked-files=all"],
                cancellationToken, throwOnNonZeroExitCode: false);
            if (status.ExitCode != 0 || !string.IsNullOrWhiteSpace(status.Stdout))
            {
                logger.LogWarning("workspace_cleanup_preserved: uncommitted files or unreadable Git status at {WorkspacePath}.", worktreePath);
                return false;
            }
            // Preserve proof of a verified clean worktree if a partial deletion removes .git.
            await File.WriteAllTextAsync(marker, worktreePath, cancellationToken);
        }
        else if (!File.Exists(marker) || await File.ReadAllTextAsync(marker, cancellationToken) != worktreePath)
        {
            logger.LogWarning("workspace_cleanup_preserved: ownership unknown for {WorkspacePath}.", worktreePath);
            return false;
        }

        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateRemovalTree(worktreePath);
            if (await TryRemoveWorktreePathAsync(sharedClonePath, worktreePath, cancellationToken))
            {
                File.Delete(marker);
                return true;
            }
            if (attempt < 3)
            {
                logger.LogWarning("workspace_cleanup_retry: {WorkspacePath} Attempt={Attempt}", worktreePath, attempt + 1);
                await Task.Delay(200 * (1 << attempt), cancellationToken);
            }
        }
        return false;
    }

    private async Task<bool> TryRemoveWorktreePathAsync(string sharedClonePath, string worktreePath, CancellationToken cancellationToken)
    {
        var gitDirectoryPath = Path.Combine(sharedClonePath, ".git");
        if (Directory.Exists(gitDirectoryPath))
        {
            var removeResult = await RunGitAsync(
                sharedClonePath,
                ["worktree", "remove", "--force", worktreePath],
                cancellationToken,
                throwOnNonZeroExitCode: false);

            if (removeResult.ExitCode != 0)
            {
                logger.LogWarning(
                    "git worktree remove failed for {WorkspacePath}. ExitCode={ExitCode}. StdErr={StdErr}",
                    worktreePath,
                    removeResult.ExitCode,
                    TruncateForLog(removeResult.Stderr, 2_000));
            }

            _ = await RunGitAsync(
                sharedClonePath,
                ["worktree", "prune"],
                cancellationToken,
                throwOnNonZeroExitCode: false);
        }

        if (!Directory.Exists(worktreePath))
        {
            return true;
        }

        try
        {
            ValidateRemovalTree(worktreePath);
            foreach (var file in Directory.EnumerateFiles(worktreePath, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"Refusing linked workspace file '{file}'.");
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
            Directory.Delete(worktreePath, recursive: true);
            return !Directory.Exists(worktreePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                ex,
                "Failed to remove workspace path {WorkspacePath} directly after git cleanup attempt.",
                worktreePath);
            return false;
        }
    }

    private async Task<GitResult> RunGitAsync(
        string? workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnNonZeroExitCode = true)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                stdout.AppendLine(args.Data);
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                stderr.AppendLine(args.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start git process.");
        }

        await using var ownedProcess = new Symphony.Infrastructure.Processes.OwnedProcessLifetime(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(exitTimeout.Token);
            }
        }

        var result = new GitResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        if (throwOnNonZeroExitCode && result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git command failed (exit {result.ExitCode}): git {string.Join(' ', arguments)}{Environment.NewLine}{result.Stderr}");
        }

        return result;
    }

    private sealed record GitResult(int ExitCode, string Stdout, string Stderr);

    private static void ValidateRemovalTree(string path)
    {
        WorkspacePathSafety.EnsureNoReparsePoints(path);
        if (!Directory.Exists(path)) return;
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Refusing workspace cleanup through linked entry '{entry.FullName}'.");
            if (entry is DirectoryInfo) ValidateRemovalTree(entry.FullName);
        }
    }

    private static void EnsureSeparatePaths(string sharedClonePath, string worktreesRoot)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var clone = sharedClonePath.TrimEnd(Path.DirectorySeparatorChar);
        var trees = worktreesRoot.TrimEnd(Path.DirectorySeparatorChar);
        if (clone.Equals(trees, comparison) || clone.StartsWith(trees + Path.DirectorySeparatorChar, comparison) ||
            trees.StartsWith(clone + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("Shared clone and worktrees root must not overlap.");
    }

    private static SemaphoreSlim GetSharedCloneLock(string sharedClonePath) =>
        SharedCloneLocks.GetOrAdd(sharedClonePath, _ => new SemaphoreSlim(1, 1));

    private static string GetSharedCloneLockPath(string sharedClonePath) =>
        $"{sharedClonePath}.lock";

    private static string GetBaseBranchSnapshotRef(string baseBranch) =>
        $"refs/symphony/origin/{baseBranch}";

    private static void WriteLockMetadata(FileStream stream, string sharedClonePath)
    {
        var metadata =
            $"pid={Environment.ProcessId}{Environment.NewLine}" +
            $"acquired_utc={DateTimeOffset.UtcNow:O}{Environment.NewLine}" +
            $"shared_clone_path={sharedClonePath}{Environment.NewLine}";
        var bytes = Encoding.UTF8.GetBytes(metadata);
        stream.SetLength(0);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void EnsureDirectoryPathAvailable(string path, string logicalName)
    {
        if (File.Exists(path))
        {
            throw new InvalidOperationException($"Configured {logicalName} path '{path}' already exists as a file.");
        }
    }

    private static string TruncateForLog(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        return $"{trimmed[..maxLength]}...";
    }
}
