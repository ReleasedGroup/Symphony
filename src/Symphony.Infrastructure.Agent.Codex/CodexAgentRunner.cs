using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;

namespace Symphony.Infrastructure.Agent.Codex;

public sealed class CodexAgentRunner(ILogger<CodexAgentRunner> logger) : IAgentRunner
{
    public async Task<AgentRunResult> RunIssueAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
        {
            throw new ArgumentException("Command must be non-empty.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.WorkspacePath))
        {
            throw new ArgumentException("WorkspacePath must be non-empty.", nameof(request));
        }

        if (!Directory.Exists(request.WorkspacePath))
        {
            throw new DirectoryNotFoundException($"Workspace path does not exist: {request.WorkspacePath}");
        }

        if (request.TimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "TimeoutMs must be > 0.");
        }

        var startInfo = BuildProcessStartInfo(request.Command, request.WorkspacePath);
        using var process = new Process { StartInfo = startInfo };
        var stdoutBuffer = new StringBuilder();
        var stderrBuffer = new StringBuilder();

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
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKillProcess(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            throw;
        }

        startedAt.Stop();
        process.WaitForExit();

        var stdout = stdoutBuffer.ToString().TrimEnd();
        var stderr = stderrBuffer.ToString().TrimEnd();

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
            // Best-effort cleanup only.
        }
    }
}
