using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;

namespace Symphony.Infrastructure.Workspaces;

public sealed class WorkspaceHookRunner(ILogger<WorkspaceHookRunner> logger) : IWorkspaceHookRunner
{
    private const int MaxCapturedOutputChars = 8_000;

    public async Task RunHookAsync(
        WorkspaceHookRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        logger.LogInformation(
            "Running workspace hook '{HookName}' for issue {IssueIdentifier}.",
            request.HookName,
            request.IssueIdentifier);

        var startInfo = BuildProcessStartInfo(request.Script, request.WorkspacePath);
        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                AppendBoundedLine(stdout, args.Data);
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                AppendBoundedLine(stderr, args.Data);
            }
        };

        var startedAt = Stopwatch.StartNew();
        if (!process.Start())
        {
            throw new WorkspaceHookExecutionException(
                request.HookName,
                $"Failed to start hook '{request.HookName}'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.TimeoutMs);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            throw new WorkspaceHookExecutionException(
                request.HookName,
                $"Workspace hook '{request.HookName}' timed out after {request.TimeoutMs}ms.",
                isTimeout: true);
        }

        startedAt.Stop();
        if (process.HasExited)
        {
            process.WaitForExit();
        }

        if (process.ExitCode != 0)
        {
            throw new WorkspaceHookExecutionException(
                request.HookName,
                $"Workspace hook '{request.HookName}' failed with exit code {process.ExitCode}. StdErr: {TrimForLog(stderr.ToString())}");
        }

        logger.LogInformation(
            "Workspace hook '{HookName}' completed in {DurationMs}ms for issue {IssueIdentifier}.",
            request.HookName,
            (int)startedAt.Elapsed.TotalMilliseconds,
            request.IssueIdentifier);
    }

    private static ProcessStartInfo BuildProcessStartInfo(string script, string workspacePath)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workspacePath
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/d /s /c {script}";
            return startInfo;
        }

        startInfo.FileName = "/bin/sh";
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(script);
        return startInfo;
    }

    private static void ValidateRequest(WorkspaceHookRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.HookName))
        {
            throw new ArgumentException("HookName must be non-empty.", nameof(request.HookName));
        }

        if (string.IsNullOrWhiteSpace(request.Script))
        {
            throw new ArgumentException("Script must be non-empty.", nameof(request.Script));
        }

        if (string.IsNullOrWhiteSpace(request.WorkspacePath))
        {
            throw new ArgumentException("WorkspacePath must be non-empty.", nameof(request.WorkspacePath));
        }

        if (!Directory.Exists(request.WorkspacePath))
        {
            throw new DirectoryNotFoundException($"Workspace path does not exist: {request.WorkspacePath}");
        }

        if (request.TimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.TimeoutMs), "TimeoutMs must be > 0.");
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort kill for timeout path.
        }
    }

    private static void AppendBoundedLine(StringBuilder builder, string line)
    {
        var remaining = MaxCapturedOutputChars - builder.Length;
        if (remaining <= 0)
        {
            return;
        }

        if (line.Length + Environment.NewLine.Length <= remaining)
        {
            builder.AppendLine(line);
            return;
        }

        var charsToCopy = Math.Max(remaining - Environment.NewLine.Length, 0);
        if (charsToCopy > 0)
        {
            builder.Append(line.AsSpan(0, Math.Min(charsToCopy, line.Length)));
            builder.AppendLine();
        }
    }

    private static string TrimForLog(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= MaxCapturedOutputChars
            ? trimmed
            : $"{trimmed[..MaxCapturedOutputChars]}...";
    }
}
