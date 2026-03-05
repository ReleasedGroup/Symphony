using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;

namespace Symphony.Infrastructure.Agent.Codex;

public sealed class CodexAgentRunner(ILogger<CodexAgentRunner> logger) : IAgentRunner
{
    private const int MaxCapturedOutputChars = 256_000;
    private const int KillGracePeriodMs = 10_000;
    private const string ClientName = "symphony";
    private const string ClientVersion = "1.0";

    public Task<AgentRunResult> RunIssueAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        if (IsLikelyAppServerCommand(request.Command))
        {
            return RunAppServerCommandAsync(request, cancellationToken);
        }

        return RunShellCommandAsync(request, cancellationToken);
    }

    private static void ValidateRequest(AgentRunRequest request)
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

        if (string.IsNullOrWhiteSpace(request.ApprovalPolicy))
        {
            throw new ArgumentException("ApprovalPolicy must be non-empty.", nameof(request.ApprovalPolicy));
        }

        if (string.IsNullOrWhiteSpace(request.ThreadSandbox))
        {
            throw new ArgumentException("ThreadSandbox must be non-empty.", nameof(request.ThreadSandbox));
        }

        if (string.IsNullOrWhiteSpace(request.TurnSandboxPolicy))
        {
            throw new ArgumentException("TurnSandboxPolicy must be non-empty.", nameof(request.TurnSandboxPolicy));
        }

        if (request.ReadTimeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ReadTimeoutMs), "ReadTimeoutMs must be > 0.");
        }
    }

    private async Task<AgentRunResult> RunShellCommandAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken)
    {
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
        TerminationOutcome termination = TerminationOutcome.NotNeeded;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            termination = await TerminateProcessAsync(process, request.IssueIdentifier, "timed-out shell command");
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

            stderr = AppendLine(stderr, $"Command timed out after {request.TimeoutMs}ms.");
            stderr = AppendTerminationDetails(stderr, termination);

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

    private async Task<AgentRunResult> RunAppServerCommandAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken)
    {
        var startInfo = BuildProcessStartInfo(request.Command, request.WorkspacePath);
        using var process = new Process { StartInfo = startInfo };
        var stdoutBuffer = new BoundedOutputBuffer(MaxCapturedOutputChars, "stdout");
        var stderrBuffer = new BoundedOutputBuffer(MaxCapturedOutputChars, "stderr");

        var startedAt = Stopwatch.StartNew();
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start command '{request.Command}'.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.TimeoutMs);
        var stderrPumpTask = PumpReaderAsync(process.StandardError, stderrBuffer, timeoutCts.Token);

        TerminationOutcome termination = TerminationOutcome.NotNeeded;
        try
        {
            await SendRequestAsync(
                process.StandardInput,
                requestId: 1,
                method: "initialize",
                @params: new
                {
                    clientInfo = new { name = ClientName, version = ClientVersion },
                    capabilities = new { }
                },
                timeoutCts.Token);

            using (var initializeResponse = await ReadResponseAsync(
                       process.StandardOutput,
                       stdoutBuffer,
                       expectedRequestId: 1,
                       readTimeoutMs: request.ReadTimeoutMs,
                       timeoutCts.Token))
            {
                EnsureNoProtocolError(initializeResponse.RootElement, "initialize");
            }

            await SendNotificationAsync(
                process.StandardInput,
                method: "initialized",
                @params: new { },
                timeoutCts.Token);

            await SendRequestAsync(
                process.StandardInput,
                requestId: 2,
                method: "thread/start",
                @params: new
                {
                    approvalPolicy = request.ApprovalPolicy,
                    sandbox = request.ThreadSandbox,
                    cwd = request.WorkspacePath
                },
                timeoutCts.Token);

            string threadId;
            using (var threadResponse = await ReadResponseAsync(
                       process.StandardOutput,
                       stdoutBuffer,
                       expectedRequestId: 2,
                       readTimeoutMs: request.ReadTimeoutMs,
                       timeoutCts.Token))
            {
                EnsureNoProtocolError(threadResponse.RootElement, "thread/start");
                threadId = GetRequiredString(threadResponse.RootElement, "result", "thread", "id");
            }

            await SendRequestAsync(
                process.StandardInput,
                requestId: 3,
                method: "turn/start",
                @params: new
                {
                    threadId,
                    input = new[]
                    {
                        new
                        {
                            type = "text",
                            text = request.Prompt
                        }
                    },
                    cwd = request.WorkspacePath,
                    title = request.IssueIdentifier,
                    approvalPolicy = request.ApprovalPolicy,
                    sandboxPolicy = new
                    {
                        type = request.TurnSandboxPolicy
                    }
                },
                timeoutCts.Token);

            using (var turnResponse = await ReadResponseAsync(
                       process.StandardOutput,
                       stdoutBuffer,
                       expectedRequestId: 3,
                       readTimeoutMs: request.ReadTimeoutMs,
                       timeoutCts.Token))
            {
                EnsureNoProtocolError(turnResponse.RootElement, "turn/start");
                _ = GetOptionalString(turnResponse.RootElement, "result", "turn", "id");
            }

            await TryShutdownAppServerAsync(
                process.StandardInput,
                process.StandardOutput,
                stdoutBuffer,
                readTimeoutMs: request.ReadTimeoutMs,
                timeoutCts.Token);

            termination = await EnsureProcessStoppedAsync(
                process,
                request.IssueIdentifier,
                gracePeriodMs: request.ReadTimeoutMs,
                context: "app-server graceful shutdown");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            termination = await TerminateProcessAsync(process, request.IssueIdentifier, "timed-out app-server run");
            await SafeAwaitReaderPumpAsync(stderrPumpTask, request.ReadTimeoutMs);
            startedAt.Stop();

            var stderr = AppendLine(stderrBuffer.ToText(), $"Command timed out after {request.TimeoutMs}ms.");
            stderr = AppendTerminationDetails(stderr, termination);

            return new AgentRunResult(
                Success: false,
                ExitCode: -1,
                Stdout: stdoutBuffer.ToText(),
                Stderr: stderr,
                Duration: startedAt.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process, out _);
            throw;
        }
        catch (Exception ex)
        {
            termination = await TerminateProcessAsync(process, request.IssueIdentifier, "app-server protocol failure");
            await SafeAwaitReaderPumpAsync(stderrPumpTask, request.ReadTimeoutMs);
            startedAt.Stop();

            var stderr = AppendLine(stderrBuffer.ToText(), $"app_server_error: {ex.Message}");
            stderr = AppendTerminationDetails(stderr, termination);

            return new AgentRunResult(
                Success: false,
                ExitCode: process.HasExited ? process.ExitCode : -2,
                Stdout: stdoutBuffer.ToText(),
                Stderr: stderr,
                Duration: startedAt.Elapsed);
        }

        await SafeAwaitReaderPumpAsync(stderrPumpTask, request.ReadTimeoutMs);
        startedAt.Stop();

        var exitCode = process.HasExited ? process.ExitCode : -1;
        var success = process.HasExited &&
                      process.ExitCode == 0 &&
                      string.IsNullOrWhiteSpace(termination.KillError) &&
                      termination.ExitedAfterKillAttempt;
        var stderrText = AppendTerminationDetails(stderrBuffer.ToText(), termination);

        return new AgentRunResult(
            Success: success,
            ExitCode: exitCode,
            Stdout: stdoutBuffer.ToText(),
            Stderr: stderrText,
            Duration: startedAt.Elapsed);
    }

    private static bool IsLikelyAppServerCommand(string command)
    {
        return command.Contains("app-server", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task TryShutdownAppServerAsync(
        StreamWriter stdin,
        StreamReader stdout,
        BoundedOutputBuffer stdoutBuffer,
        int readTimeoutMs,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendRequestAsync(stdin, 4, "shutdown", new { }, cancellationToken);
            using var _ = await ReadResponseAsync(stdout, stdoutBuffer, 4, readTimeoutMs, cancellationToken);
        }
        catch
        {
            // Best-effort shutdown path.
        }

        try
        {
            await SendNotificationAsync(stdin, "exit", null, cancellationToken);
            stdin.Close();
        }
        catch
        {
            // Best-effort shutdown path.
        }
    }

    private async Task<TerminationOutcome> EnsureProcessStoppedAsync(
        Process process,
        string issueIdentifier,
        int gracePeriodMs,
        string context)
    {
        if (process.HasExited)
        {
            return TerminationOutcome.NotNeeded;
        }

        var exitedGracefully = await WaitForExitWithGracePeriodAsync(process, gracePeriodMs);
        if (exitedGracefully)
        {
            return TerminationOutcome.NotNeeded;
        }

        logger.LogWarning(
            "Process for issue {IssueIdentifier} did not exit during {Context}; forcing termination.",
            issueIdentifier,
            context);

        return await TerminateProcessAsync(process, issueIdentifier, context);
    }

    private async Task<TerminationOutcome> TerminateProcessAsync(
        Process process,
        string issueIdentifier,
        string context)
    {
        var killError = string.Empty;
        var exitedAfterKillAttempt = true;

        if (!TryKillProcess(process, out killError))
        {
            logger.LogWarning(
                "Failed to terminate process for issue {IssueIdentifier} during {Context}. Error: {Error}",
                issueIdentifier,
                context,
                killError);
        }

        if (!process.HasExited)
        {
            exitedAfterKillAttempt = await WaitForExitWithGracePeriodAsync(process, KillGracePeriodMs);
            if (!exitedAfterKillAttempt)
            {
                logger.LogWarning(
                    "Process for issue {IssueIdentifier} did not exit within kill grace period ({KillGracePeriodMs}ms). Context={Context}",
                    issueIdentifier,
                    KillGracePeriodMs,
                    context);
            }
        }

        return new TerminationOutcome(
            ExitedAfterKillAttempt: exitedAfterKillAttempt,
            KillError: killError);
    }

    private static async Task SafeAwaitReaderPumpAsync(Task readerPumpTask, int maxWaitMs)
    {
        try
        {
            var completedTask = await Task.WhenAny(readerPumpTask, Task.Delay(maxWaitMs));
            if (completedTask != readerPumpTask)
            {
                return;
            }

            await readerPumpTask;
        }
        catch
        {
            // Reader pump failures are non-fatal for command results.
        }
    }

    private static async Task PumpReaderAsync(
        StreamReader reader,
        BoundedOutputBuffer buffer,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            buffer.AppendLine(line);
        }
    }

    private static async Task SendRequestAsync(
        StreamWriter writer,
        int requestId,
        string method,
        object? @params,
        CancellationToken cancellationToken)
    {
        var payload = @params is null
            ? JsonSerializer.Serialize(new { id = requestId, method })
            : JsonSerializer.Serialize(new { id = requestId, method, @params });

        await WriteJsonLineAsync(writer, payload, cancellationToken);
    }

    private static async Task SendNotificationAsync(
        StreamWriter writer,
        string method,
        object? @params,
        CancellationToken cancellationToken)
    {
        var payload = @params is null
            ? JsonSerializer.Serialize(new { method })
            : JsonSerializer.Serialize(new { method, @params });

        await WriteJsonLineAsync(writer, payload, cancellationToken);
    }

    private static async Task WriteJsonLineAsync(
        StreamWriter writer,
        string payload,
        CancellationToken cancellationToken)
    {
        await writer.WriteAsync(payload.AsMemory(), cancellationToken);
        await writer.WriteAsync(Environment.NewLine.AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static async Task<JsonDocument> ReadResponseAsync(
        StreamReader reader,
        BoundedOutputBuffer stdoutBuffer,
        int expectedRequestId,
        int readTimeoutMs,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCts.CancelAfter(readTimeoutMs);

            string? line;
            try
            {
                line = await reader.ReadLineAsync(readCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for app-server response to request id {expectedRequestId}.");
            }

            if (line is null)
            {
                throw new InvalidOperationException($"App-server stdout closed before response id {expectedRequestId} was received.");
            }

            stdoutBuffer.AppendLine(line);

            JsonDocument parsed;
            try
            {
                parsed = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (!TryGetResponseId(parsed.RootElement, out var responseId) || responseId != expectedRequestId)
            {
                parsed.Dispose();
                continue;
            }

            return parsed;
        }
    }

    private static bool TryGetResponseId(JsonElement root, out int responseId)
    {
        responseId = 0;
        if (!root.TryGetProperty("id", out var idProperty))
        {
            return false;
        }

        if (idProperty.ValueKind == JsonValueKind.Number)
        {
            return idProperty.TryGetInt32(out responseId);
        }

        if (idProperty.ValueKind == JsonValueKind.String)
        {
            return int.TryParse(idProperty.GetString(), out responseId);
        }

        return false;
    }

    private static void EnsureNoProtocolError(JsonElement response, string operationName)
    {
        if (!response.TryGetProperty("error", out var errorProperty) ||
            errorProperty.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        throw new InvalidOperationException($"app_server_protocol_error:{operationName}:{errorProperty}");
    }

    private static string GetRequiredString(JsonElement root, params string[] path)
    {
        if (!TryGetNestedElement(root, out var value, path))
        {
            throw new InvalidOperationException($"Missing required protocol field '{string.Join('.', path)}'.");
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"Protocol field '{string.Join('.', path)}' must be a string.");
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"Protocol field '{string.Join('.', path)}' must be non-empty.");
        }

        return text;
    }

    private static string? GetOptionalString(JsonElement root, params string[] path)
    {
        if (!TryGetNestedElement(root, out var value, path))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static bool TryGetNestedElement(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }

        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return false;
        }

        return true;
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

    private static string AppendTerminationDetails(string stderr, TerminationOutcome termination)
    {
        if (termination == TerminationOutcome.NotNeeded)
        {
            return stderr;
        }

        if (!termination.ExitedAfterKillAttempt)
        {
            stderr = AppendLine(stderr, $"Process did not exit within kill grace period ({KillGracePeriodMs}ms).");
        }

        if (!string.IsNullOrWhiteSpace(termination.KillError))
        {
            stderr = AppendLine(stderr, $"Kill error: {termination.KillError}");
        }

        return stderr;
    }

    private static string AppendLine(string value, string line)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return line;
        }

        return $"{value}{Environment.NewLine}{line}";
    }

    private readonly record struct TerminationOutcome(bool ExitedAfterKillAttempt, string KillError)
    {
        public static readonly TerminationOutcome NotNeeded = new(true, string.Empty);
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
