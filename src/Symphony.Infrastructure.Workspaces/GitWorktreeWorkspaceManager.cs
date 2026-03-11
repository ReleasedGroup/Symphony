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

        EnsureDirectoryPathAvailable(rootPath, "workspace.root");
        EnsureDirectoryPathAvailable(worktreesRootPath, "workspace.worktrees_root");
        EnsureDirectoryPathAvailable(sharedClonePath, "workspace.shared_clone_path");
        EnsureDirectoryPathAvailable(worktreePath, $"workspace for issue '{request.IssueIdentifier}'");
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(worktreesRootPath);

        await EnsureSharedCloneAsync(sharedClonePath, request.RemoteRepositoryUrl, cancellationToken);
        await EnsureLatestFromOriginAsync(sharedClonePath, request.BaseBranch, cancellationToken);

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
                    ["worktree", "add", "-b", branchName, worktreePath, $"origin/{request.BaseBranch}"],
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

        if (File.Exists(worktreePath))
        {
            throw new InvalidOperationException($"Workspace path collision detected for issue '{request.IssueIdentifier}'.");
        }

        if (!Directory.Exists(worktreePath))
        {
            return new WorkspaceCleanupResult(worktreePath, Existed: false, RemovedNow: false);
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

        var removedNow = await RemoveWorktreePathAsync(sharedClonePath, worktreePath, cancellationToken);
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
            await RunGitAsync(sharedClonePath, ["remote", "set-url", "origin", remoteRepositoryUrl], cancellationToken);
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

    private async Task EnsureLatestFromOriginAsync(string sharedClonePath, string baseBranch, CancellationToken cancellationToken)
    {
        await RunGitAsync(sharedClonePath, ["fetch", "origin", "--prune"], cancellationToken);
        await RunGitAsync(sharedClonePath, ["fetch", "origin", baseBranch], cancellationToken);
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
            Directory.Delete(worktreePath, recursive: true);
            return !Directory.Exists(worktreePath);
        }
        catch (Exception ex)
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

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        var result = new GitResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        if (throwOnNonZeroExitCode && result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git command failed (exit {result.ExitCode}): git {string.Join(' ', arguments)}{Environment.NewLine}{result.Stderr}");
        }

        return result;
    }

    private sealed record GitResult(int ExitCode, string Stdout, string Stderr);

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
