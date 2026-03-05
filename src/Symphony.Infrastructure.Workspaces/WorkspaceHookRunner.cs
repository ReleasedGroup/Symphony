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
    private const string TempHookDirectoryName = ".symphony-hooks";

    public async Task RunHookAsync(
        WorkspaceHookRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        logger.LogInformation(
            "Running workspace hook '{HookName}' for issue {IssueIdentifier}.",
            request.HookName,
            request.IssueIdentifier);

        var scriptFilePath = await CreateHookScriptFileAsync(request, cancellationToken);
        var startedAt = Stopwatch.StartNew();

        try
        {
            var startInfo = BuildProcessStartInfo(scriptFilePath, request.WorkspacePath);
            using var process = new Process { StartInfo = startInfo };
            var stdoutBuffer = new BoundedOutputBuffer(MaxCapturedOutputChars, "stdout");
            var stderrBuffer = new BoundedOutputBuffer(MaxCapturedOutputChars, "stderr");

            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    stdoutBuffer.AppendLine(args.Data);
                }
            };

            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    stderrBuffer.AppendLine(args.Data);
                }
            };

            try
            {
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
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    TryKillProcess(process);
                    throw new WorkspaceHookExecutionException(
                        request.HookName,
                        $"Workspace hook '{request.HookName}' timed out after {request.TimeoutMs}ms.",
                        isTimeout: true,
                        innerException: ex);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    TryKillProcess(process);
                    throw;
                }

                if (process.HasExited)
                {
                    process.WaitForExit();
                }

                if (process.ExitCode != 0)
                {
                    throw new WorkspaceHookExecutionException(
                        request.HookName,
                        $"Workspace hook '{request.HookName}' failed with exit code {process.ExitCode}. StdErr: {stderrBuffer.ToText()}");
                }
            }
            catch (WorkspaceHookExecutionException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                TryKillProcess(process);
                throw new WorkspaceHookExecutionException(
                    request.HookName,
                    $"Workspace hook '{request.HookName}' failed unexpectedly.",
                    innerException: ex);
            }

            logger.LogInformation(
                "Workspace hook '{HookName}' completed in {DurationMs}ms for issue {IssueIdentifier}.",
                request.HookName,
                (int)startedAt.Elapsed.TotalMilliseconds,
                request.IssueIdentifier);
        }
        finally
        {
            startedAt.Stop();
            TryDeleteFile(scriptFilePath);
        }
    }

    private static async Task<string> CreateHookScriptFileAsync(
        WorkspaceHookRequest request,
        CancellationToken cancellationToken)
    {
        var hookDirectory = Path.Combine(request.WorkspacePath, TempHookDirectoryName);
        Directory.CreateDirectory(hookDirectory);

        var extension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".cmd" : ".sh";
        var scriptFilePath = Path.Combine(hookDirectory, $"{Guid.NewGuid():N}{extension}");
        var scriptContent = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"@echo off{Environment.NewLine}{request.Script}{Environment.NewLine}"
            : $"{request.Script}{Environment.NewLine}";

        await File.WriteAllTextAsync(scriptFilePath, scriptContent, cancellationToken);
        return scriptFilePath;
    }

    private static ProcessStartInfo BuildProcessStartInfo(string scriptFilePath, string workspacePath)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workspacePath
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(scriptFilePath);
            return startInfo;
        }

        startInfo.FileName = "/bin/sh";
        startInfo.ArgumentList.Add(scriptFilePath);
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
            // Best-effort kill path.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup for temp hook scripts.
        }
    }

    private sealed class BoundedOutputBuffer(int maxChars, string streamName)
    {
        private readonly object _sync = new();
        private readonly StringBuilder _builder = new();
        private bool _truncated;

        public void AppendLine(string line)
        {
            lock (_sync)
            {
                if (_truncated)
                {
                    return;
                }

                var remaining = maxChars - _builder.Length;
                if (remaining <= 0)
                {
                    _truncated = true;
                    return;
                }

                if (line.Length + Environment.NewLine.Length <= remaining)
                {
                    _builder.AppendLine(line);
                    return;
                }

                var charsToCopy = Math.Max(remaining - Environment.NewLine.Length, 0);
                if (charsToCopy > 0)
                {
                    _builder.Append(line.AsSpan(0, Math.Min(charsToCopy, line.Length)));
                    _builder.AppendLine();
                }

                _truncated = true;
            }
        }

        public string ToText()
        {
            lock (_sync)
            {
                var text = _builder.ToString().TrimEnd();
                if (!_truncated)
                {
                    return text;
                }

                var suffix = $"[{streamName} truncated at {maxChars} chars]";
                if (string.IsNullOrWhiteSpace(text))
                {
                    return suffix;
                }

                return $"{text}{Environment.NewLine}{suffix}";
            }
        }
    }
}
