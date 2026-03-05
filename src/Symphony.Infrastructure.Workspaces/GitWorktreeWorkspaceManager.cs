using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;

namespace Symphony.Infrastructure.Workspaces;

public sealed partial class GitWorktreeWorkspaceManager(
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

        var rootPath = GetAbsolutePath(request.WorkspaceRoot);
        var sharedClonePath = GetAbsolutePath(request.SharedClonePath);
        var worktreesRootPath = GetAbsolutePath(request.WorktreesRoot);
        var issueKey = SanitizeIssueIdentifier(request.IssueIdentifier);
        var worktreePath = Path.Combine(worktreesRootPath, issueKey);
        var branchName = string.IsNullOrWhiteSpace(request.SuggestedBranchName)
            ? $"symphony/{issueKey}"
            : request.SuggestedBranchName.Trim();

        EnsurePathIsWithinRoot(rootPath, sharedClonePath);
        EnsurePathIsWithinRoot(rootPath, worktreesRootPath);
        EnsurePathIsWithinRoot(worktreesRootPath, worktreePath);

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

        logger.LogInformation(
            "Prepared workspace {WorkspacePath} (branch {BranchName}) for issue {IssueIdentifier}.",
            worktreePath,
            branchName,
            request.IssueIdentifier);

        return new WorkspacePreparationResult(worktreePath, branchName, CreatedNow: true);
    }

    private static string GetAbsolutePath(string path) =>
        Path.GetFullPath(path);

    private static void EnsurePathIsWithinRoot(string rootPath, string candidatePath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(candidatePath);
        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !candidate.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path '{candidatePath}' must be within root '{rootPath}'.");
        }
    }

    private static string SanitizeIssueIdentifier(string issueIdentifier)
    {
        var input = string.IsNullOrWhiteSpace(issueIdentifier) ? "issue" : issueIdentifier.Trim();
        var sanitized = IssueIdentifierRegex().Replace(input, "-").Trim('-');
        return string.IsNullOrWhiteSpace(sanitized) ? "issue" : sanitized.ToLowerInvariant();
    }

    private async Task EnsureSharedCloneAsync(
        string sharedClonePath,
        string remoteRepositoryUrl,
        CancellationToken cancellationToken)
    {
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

    [GeneratedRegex(@"[^a-zA-Z0-9._-]+")]
    private static partial Regex IssueIdentifierRegex();
}
