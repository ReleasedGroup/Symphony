using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;

namespace Symphony.Infrastructure.Agent.Codex;

public sealed class CodexAgentRunner(ILogger<CodexAgentRunner> logger) : IAgentRunner
{
    private const int MaxCapturedOutputChars = 256_000;
    private const int KillGracePeriodMs = 10_000;

    public async Task<AgentRunResult> RunIssueAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
        {
            throw new ArgumentException("Command must be non-empty.", nameof(request.Command));
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

        var startInfo = BuildProcessStartInfo(request.Command, request.WorkspacePath);
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

        var startedAt = Stopwatch.StartNew();
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start command '{request.Command}'.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteAsync(request.Prompt.AsMemory(), cancellationToken);
                await process.StandardInput.FlushAsync(cancellationToken);
            }
        }
        catch (IOException) when (process.HasExited)
        {
            // Process exited before consuming stdin.
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // Process exited before consuming stdin.
        }
        finally
        {
            process.StandardInput.Close();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.TimeoutMs);

        var timedOut = false;
        var killError = string.Empty;
        var exitedAfterKillAttempt = true;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;

            if (!TryKillProcess(process, out killError))
            {
                logger.LogWarning(
                    "Failed to terminate timed-out process for issue {IssueIdentifier}. Error: {Error}",
                    request.IssueIdentifier,
                    killError);
            }

            if (!process.HasExited)
            {
                exitedAfterKillAttempt = await WaitForExitWithGracePeriodAsync(process, KillGracePeriodMs);
                if (!exitedAfterKillAttempt)
                {
                    logger.LogWarning(
                        "Timed-out process for issue {IssueIdentifier} did not exit within kill grace period ({KillGracePeriodMs}ms).",
                        request.IssueIdentifier,
                        KillGracePeriodMs);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process, out _);
            throw;
        }

        startedAt.Stop();
        if (process.HasExited)
        {
            process.WaitForExit();
        }

        var stdout = stdoutBuffer.ToText();
        var stderr = stderrBuffer.ToText();

        if (timedOut)
        {
            logger.LogWarning(
                "Agent command timed out for issue {IssueIdentifier} after {TimeoutMs}ms.",
                request.IssueIdentifier,
                request.TimeoutMs);

            if (stderr.Length == 0)
            {
                stderr = $"Command timed out after {request.TimeoutMs}ms.";
            }
            else
            {
                stderr = $"{stderr}{Environment.NewLine}Command timed out after {request.TimeoutMs}ms.";
            }

            if (!exitedAfterKillAttempt)
            {
                stderr = $"{stderr}{Environment.NewLine}Process did not exit within kill grace period ({KillGracePeriodMs}ms).";
            }

            if (!string.IsNullOrWhiteSpace(killError))
            {
                stderr = $"{stderr}{Environment.NewLine}Kill error: {killError}";
            }

            return new AgentRunResult(
                Success: false,
                ExitCode: -1,
                Stdout: stdout,
                Stderr: stderr,
                Duration: startedAt.Elapsed);
        }

        var success = process.ExitCode == 0;
        return new AgentRunResult(
            Success: success,
            ExitCode: process.ExitCode,
            Stdout: stdout,
            Stderr: stderr,
            Duration: startedAt.Elapsed);
    }

    private static ProcessStartInfo BuildProcessStartInfo(string command, string workspacePath)
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
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);
            return startInfo;
        }

        startInfo.FileName = "/bin/bash";
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private static bool TryKillProcess(Process process, out string error)
    {
        error = string.Empty;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static async Task<bool> WaitForExitWithGracePeriodAsync(Process process, int gracePeriodMs)
    {
        using var graceCts = new CancellationTokenSource(gracePeriodMs);
        try
        {
            await process.WaitForExitAsync(graceCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
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
