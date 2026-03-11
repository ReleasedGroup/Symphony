using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;

namespace Symphony.Infrastructure.Agent.Codex;

public sealed partial class CodexAgentRunner(
    ITrackerClient trackerClient,
    ILogger<CodexAgentRunner> logger) : IAgentRunner
{
    private const int MaxCapturedOutputChars = 256_000;
    private const int KillGracePeriodMs = 10_000;
    private const string ClientName = "symphony";
    private const string ClientVersion = "1.0";

    public Task<AgentRunResult> RunIssueAsync(
        AgentRunRequest request,
        Func<AgentRunUpdate, CancellationToken, Task>? onUpdate = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        if (IsLikelyAppServerCommand(request.Command))
        {
            return RunAppServerCommandAsync(request, onUpdate, cancellationToken);
        }

        return RunShellCommandAsync(request, onUpdate, cancellationToken);
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

        if (request.MaxTurns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaxTurns), "MaxTurns must be > 0.");
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
        Func<AgentRunUpdate, CancellationToken, Task>? onUpdate,
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

        await ReportUpdateAsync(
            onUpdate,
            new AgentRunUpdate(
                EventType: "process_started",
                Timestamp: DateTimeOffset.UtcNow,
                CodexAppServerPid: process.Id,
                Message: "Started shell command."),
            cancellationToken);

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
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
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
            stderr = AppendLine(stderr, $"Command timed out after {request.TimeoutMs}ms.");
            stderr = AppendTerminationDetails(stderr, termination);

            return new AgentRunResult(
                Success: false,
                ExitCode: -1,
                Stdout: stdout,
                Stderr: stderr,
                Duration: startedAt.Elapsed,
                ErrorCode: "turn_timeout");
        }

        var success = process.ExitCode == 0;
        await ReportUpdateAsync(
            onUpdate,
            new AgentRunUpdate(
                EventType: success ? "process_completed" : "process_failed",
                Timestamp: DateTimeOffset.UtcNow,
                CodexAppServerPid: process.Id,
                Message: $"Shell command exited with code {process.ExitCode}."),
            cancellationToken);

        return new AgentRunResult(
            Success: success,
            ExitCode: process.ExitCode,
            Stdout: stdout,
            Stderr: stderr,
            Duration: startedAt.Elapsed,
            ErrorCode: success ? null : "process_failed");
    }

    private async Task<AgentRunResult> RunAppServerCommandAsync(
        AgentRunRequest request,
        Func<AgentRunUpdate, CancellationToken, Task>? onUpdate,
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

        await ReportUpdateAsync(
            onUpdate,
            new AgentRunUpdate(
                EventType: "process_started",
                Timestamp: DateTimeOffset.UtcNow,
                CodexAppServerPid: process.Id,
                Message: "Started app-server process."),
            cancellationToken);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stderrPumpTask = PumpReaderAsync(process.StandardError, stderrBuffer, sessionCts.Token);
        var protocolReader = new ProtocolReader(
            process.StandardOutput,
            stdoutBuffer,
            process.Id,
            request.TrackerQuery?.ApiKey);
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
                    capabilities = BuildCapabilities(request)
                },
                cancellationToken);

            using (var initializeResponse = await ReadResponseAsync(
                       protocolReader,
                       expectedRequestId: 1,
                       onUpdate,
                       request.ReadTimeoutMs,
                       cancellationToken))
            {
                EnsureNoProtocolError(initializeResponse.RootElement, "initialize");
            }

            await SendNotificationAsync(process.StandardInput, "initialized", new { }, cancellationToken);

            await SendRequestAsync(
                process.StandardInput,
                requestId: 2,
                method: "thread/start",
                @params: new
                {
                    approvalPolicy = request.ApprovalPolicy,
                    sandbox = CodexProtocolValueNormalizer.NormalizeThreadSandbox(request.ThreadSandbox),
                    cwd = request.WorkspacePath,
                    tools = BuildAdvertisedTools(request)
                },
                cancellationToken);

            string threadId;
            using (var threadResponse = await ReadResponseAsync(
                       protocolReader,
                       expectedRequestId: 2,
                       onUpdate,
                       request.ReadTimeoutMs,
                       cancellationToken))
            {
                EnsureNoProtocolError(threadResponse.RootElement, "thread/start");
                threadId = GetRequiredString(threadResponse.RootElement, "result", "thread", "id");
            }

            for (var turnNumber = 1; turnNumber <= request.MaxTurns; turnNumber++)
            {
                var turnPrompt = turnNumber == 1
                    ? request.Prompt
                    : BuildContinuationPrompt(request, turnNumber);

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
                                text = turnPrompt
                            }
                        },
                        cwd = request.WorkspacePath,
                        title = $"{request.IssueIdentifier}: {request.IssueTitle}",
                        approvalPolicy = request.ApprovalPolicy,
                        sandboxPolicy = new
                        {
                            type = CodexProtocolValueNormalizer.GetTurnSandboxPolicyType(request.TurnSandboxPolicy)
                        }
                    },
                    cancellationToken);

                string? turnId;
                using (var turnResponse = await ReadResponseAsync(
                           protocolReader,
                           expectedRequestId: 3,
                           onUpdate,
                           request.ReadTimeoutMs,
                           cancellationToken))
                {
                    EnsureNoProtocolError(turnResponse.RootElement, "turn/start");
                    turnId = GetOptionalString(turnResponse.RootElement, "result", "turn", "id");
                }

                await ReportUpdateAsync(
                    onUpdate,
                    new AgentRunUpdate(
                        EventType: "session_started",
                        Timestamp: DateTimeOffset.UtcNow,
                        ThreadId: threadId,
                        TurnId: turnId,
                        CodexAppServerPid: process.Id,
                        Message: $"Codex turn {turnNumber} started."),
                    cancellationToken);

                await StreamTurnAsync(
                    protocolReader,
                    process.StandardInput,
                    request,
                    threadId,
                    turnId,
                    turnNumber,
                    onUpdate,
                    cancellationToken);

                if (turnNumber >= request.MaxTurns)
                {
                    break;
                }

                var refreshedState = await RefreshIssueStateAsync(request, cancellationToken);
                if (!string.IsNullOrWhiteSpace(refreshedState) &&
                    !IssueStateMatcher.MatchesConfiguredActiveState(
                        refreshedState,
                        request.TrackerQuery?.ActiveStates ?? []))
                {
                    break;
                }
            }

            await TryShutdownAppServerAsync(
                process.StandardInput,
                protocolReader,
                onUpdate,
                request.ReadTimeoutMs,
                CancellationToken.None);

            sessionCts.Cancel();
            termination = await EnsureProcessStoppedAsync(
                process,
                request.IssueIdentifier,
                gracePeriodMs: request.ReadTimeoutMs,
                context: "app-server graceful shutdown");
        }
        catch (RunnerFailureException ex)
        {
            termination = await TerminateProcessAsync(process, request.IssueIdentifier, ex.Code);
            sessionCts.Cancel();
            await SafeAwaitReaderPumpAsync(stderrPumpTask, request.ReadTimeoutMs);
            startedAt.Stop();

            var stderr = AppendLine(stderrBuffer.ToText(), $"{ex.Code}: {SecretRedactor.Redact(ex.Message, request.TrackerQuery?.ApiKey)}");
            stderr = AppendTerminationDetails(stderr, termination);

            return new AgentRunResult(
                Success: false,
                ExitCode: process.HasExited ? process.ExitCode : -2,
                Stdout: stdoutBuffer.ToText(),
                Stderr: stderr,
                Duration: startedAt.Elapsed,
                ErrorCode: ex.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process, out _);
            throw;
        }
        catch (Exception ex)
        {
            termination = await TerminateProcessAsync(process, request.IssueIdentifier, "response_error");
            sessionCts.Cancel();
            await SafeAwaitReaderPumpAsync(stderrPumpTask, request.ReadTimeoutMs);
            startedAt.Stop();

            var stderr = AppendLine(
                stderrBuffer.ToText(),
                $"response_error: {SecretRedactor.Redact(ex.Message, request.TrackerQuery?.ApiKey)}");
            stderr = AppendTerminationDetails(stderr, termination);

            return new AgentRunResult(
                Success: false,
                ExitCode: process.HasExited ? process.ExitCode : -2,
                Stdout: stdoutBuffer.ToText(),
                Stderr: stderr,
                Duration: startedAt.Elapsed,
                ErrorCode: "response_error");
        }

        await SafeAwaitReaderPumpAsync(stderrPumpTask, request.ReadTimeoutMs);
        startedAt.Stop();

        var exitCode = process.HasExited ? process.ExitCode : -1;
        var success = process.HasExited &&
                      process.ExitCode == 0 &&
                      string.IsNullOrWhiteSpace(termination.KillError) &&
                      termination.ExitedAfterKillAttempt;

        await ReportUpdateAsync(
            onUpdate,
            new AgentRunUpdate(
                EventType: success ? "process_completed" : "process_failed",
                Timestamp: DateTimeOffset.UtcNow,
                CodexAppServerPid: process.Id,
                Message: $"App-server exited with code {exitCode}."),
            cancellationToken);

        return new AgentRunResult(
            Success: success,
            ExitCode: exitCode,
            Stdout: stdoutBuffer.ToText(),
            Stderr: AppendTerminationDetails(stderrBuffer.ToText(), termination),
            Duration: startedAt.Elapsed,
            ErrorCode: success ? null : "process_failed");
    }

    private async Task StreamTurnAsync(
        ProtocolReader protocolReader,
        StreamWriter stdin,
        AgentRunRequest request,
        string threadId,
        string? turnId,
        int turnNumber,
        Func<AgentRunUpdate, CancellationToken, Task>? onUpdate,
        CancellationToken cancellationToken)
    {
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        turnCts.CancelAfter(request.TimeoutMs);

        while (true)
        {
            ProtocolLine protocolLine;
            try
            {
                protocolLine = await protocolReader.ReadNextAsync(turnCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RunnerFailureException("turn_timeout", $"Turn {turnNumber} timed out after {request.TimeoutMs}ms.");
            }

            if (protocolLine.IsEndOfStream)
            {
                throw new RunnerFailureException("subprocess_exit", "App-server stdout closed during turn processing.");
            }

            if (protocolLine.RawLine is null)
            {
                continue;
            }

            if (protocolLine.Document is null)
            {
                await ReportUpdateAsync(
                    onUpdate,
                    new AgentRunUpdate(
                        EventType: "malformed",
                        Timestamp: DateTimeOffset.UtcNow,
                        ThreadId: threadId,
                        TurnId: turnId,
                        CodexAppServerPid: protocolReader.ProcessId,
                        Message: protocolLine.RawLine,
                        DataJson: JsonSerializer.Serialize(new { raw = SecretRedactor.Redact(protocolLine.RawLine, request.TrackerQuery?.ApiKey) })),
                    cancellationToken);
                continue;
            }

            using (protocolLine.Document)
            {
                await HandleProtocolMessageAsync(
                    protocolLine.Document.RootElement,
                    stdin,
                    request,
                    threadId,
                    turnId,
                    onUpdate,
                    cancellationToken);

                if (IsTurnCompletedEvent(GetEventName(protocolLine.Document.RootElement)))
                {
                    return;
                }
            }
        }
    }

    private async Task HandleProtocolMessageAsync(
        JsonElement message,
        StreamWriter stdin,
        AgentRunRequest request,
        string threadId,
        string? turnId,
        Func<AgentRunUpdate, CancellationToken, Task>? onUpdate,
        CancellationToken cancellationToken)
    {
        var eventName = GetEventName(message);
        var update = CreateProtocolUpdate(message, eventName, threadId, turnId, request.TrackerQuery?.ApiKey);

        if (IsTurnCompletedEvent(eventName))
        {
            await ReportUpdateAsync(
                onUpdate,
                update with
                {
                    EventType = "turn_completed",
                    Message = string.IsNullOrWhiteSpace(update.Message) ? "Turn completed." : update.Message
                },
                cancellationToken);
            return;
        }

        if (IsTurnFailedEvent(eventName))
        {
            await ReportUpdateAsync(
                onUpdate,
                update with
                {
                    EventType = "turn_failed",
                    Message = string.IsNullOrWhiteSpace(update.Message) ? "Turn failed." : update.Message
                },
                cancellationToken);

            throw new RunnerFailureException("turn_failed", update.Message ?? "Turn failed.");
        }

        if (IsTurnCancelledEvent(eventName))
        {
            await ReportUpdateAsync(
                onUpdate,
                update with
                {
                    EventType = "turn_cancelled",
                    Message = string.IsNullOrWhiteSpace(update.Message) ? "Turn cancelled." : update.Message
                },
                cancellationToken);

            throw new RunnerFailureException("turn_cancelled", update.Message ?? "Turn cancelled.");
        }

        if (IsUserInputRequired(message, eventName))
        {
            await ReportUpdateAsync(
                onUpdate,
                update with
                {
                    EventType = "turn_input_required",
                    Message = string.IsNullOrWhiteSpace(update.Message)
                        ? "Turn requested user input."
                        : update.Message
                },
                cancellationToken);

            throw new RunnerFailureException("turn_input_required", update.Message ?? "Turn requested user input.");
        }

        if (TryGetApprovalRequest(message, eventName, out var approvalRequestId))
        {
            await SendResponseAsync(stdin, approvalRequestId!, new { approved = true }, cancellationToken);
            await ReportUpdateAsync(
                onUpdate,
                update with
                {
                    EventType = "approval_auto_approved",
                    Message = "Approved request automatically according to the configured v1 policy."
                },
                cancellationToken);
            return;
        }

        if (TryGetToolCall(message, eventName, out var toolCall))
        {
            var toolResult = await ExecuteToolCallAsync(request, toolCall, cancellationToken);
            await SendResponseAsync(stdin, toolCall.RequestId, toolResult, cancellationToken);

            var successProperty = toolResult.GetType().GetProperty("success");
            var toolSucceeded = successProperty?.GetValue(toolResult) as bool? == true;
            var unsupportedToolCall = IsUnsupportedToolCall(toolResult);

            await ReportUpdateAsync(
                onUpdate,
                update with
                {
                    EventType = toolSucceeded
                        ? "tool_call_succeeded"
                        : unsupportedToolCall
                            ? "unsupported_tool_call"
                            : "tool_call_failed",
                    Message = toolSucceeded
                        ? $"Handled tool call '{toolCall.Name}'."
                        : unsupportedToolCall
                            ? $"Tool call '{toolCall.Name}' is not supported."
                            : $"Tool call '{toolCall.Name}' failed.",
                    DataJson = SecretRedactor.Redact(JsonSerializer.Serialize(toolResult), request.TrackerQuery?.ApiKey)
                },
                cancellationToken);
            return;
        }

        await ReportUpdateAsync(
            onUpdate,
            update with
            {
                EventType = string.Equals(eventName, "notification", StringComparison.OrdinalIgnoreCase)
                    ? "notification"
                    : "other_message"
            },
            cancellationToken);
    }

    private async Task<string?> RefreshIssueStateAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken)
    {
        if (request.TrackerQuery is null)
        {
            return null;
        }

        try
        {
            var refreshedStates = await trackerClient.FetchIssueStatesByIdsAsync(
                request.TrackerQuery,
                [request.IssueId],
                cancellationToken);

            return refreshedStates.FirstOrDefault()?.State;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new RunnerFailureException(
                "issue_state_refresh_error",
                SecretRedactor.Redact(ex.Message, request.TrackerQuery.ApiKey) ?? "Issue state refresh failed.",
                ex);
        }
    }

    private async Task<object> ExecuteToolCallAsync(
        AgentRunRequest request,
        ToolCallRequest toolCall,
        CancellationToken cancellationToken)
    {
        if (!toolCall.Name.Equals("github_graphql", StringComparison.OrdinalIgnoreCase))
        {
            return new { success = false, error = "unsupported_tool_call" };
        }

        if (request.TrackerQuery is null)
        {
            return new { success = false, error = "missing_tracker_auth" };
        }

        var parseResult = ParseGitHubGraphQlToolInput(toolCall.InputJson);
        if (!parseResult.Success)
        {
            return new { success = false, error = parseResult.ErrorCode, message = parseResult.ErrorMessage };
        }

        var executionResult = await trackerClient.ExecuteGitHubGraphQlAsync(
            request.TrackerQuery,
            parseResult.Query!,
            parseResult.VariablesJson,
            cancellationToken);

        var payload = TryParseJson(executionResult.PayloadJson);
        return new
        {
            success = executionResult.Success,
            error = executionResult.ErrorCode,
            message = executionResult.ErrorMessage,
            payload
        };
    }

    private static (bool Success, string? Query, string? VariablesJson, string? ErrorCode, string? ErrorMessage) ParseGitHubGraphQlToolInput(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return (false, null, null, "invalid_graphql_input", "Tool input must be a raw GraphQL string or an object with query/variables.");
        }

        try
        {
            using var document = JsonDocument.Parse(inputJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                var shorthandQuery = root.GetString();
                return string.IsNullOrWhiteSpace(shorthandQuery)
                    ? (false, null, null, "invalid_graphql_input", "GraphQL query must be non-empty.")
                    : (true, shorthandQuery, null, null, null);
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return (false, null, null, "invalid_graphql_input", "Tool input must be a JSON object.");
            }

            var query = GetOptionalString(root, "query");
            if (string.IsNullOrWhiteSpace(query))
            {
                return (false, null, null, "invalid_graphql_input", "Tool input must include a non-empty 'query'.");
            }

            string? variablesJson = null;
            if (root.TryGetProperty("variables", out var variablesElement) &&
                variablesElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                if (variablesElement.ValueKind != JsonValueKind.Object)
                {
                    return (false, null, null, "invalid_graphql_input", "Tool input 'variables' must be a JSON object.");
                }

                variablesJson = variablesElement.GetRawText();
            }

            return (true, query, variablesJson, null, null);
        }
        catch (JsonException ex)
        {
            return (false, null, null, "invalid_graphql_input", ex.Message);
        }
    }

    private static object? TryParseJson(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return payloadJson;
        }
    }

    private static bool IsUnsupportedToolCall(object toolResult)
    {
        var errorProperty = toolResult.GetType().GetProperty("error");
        var errorCode = errorProperty?.GetValue(toolResult) as string;
        return string.Equals(errorCode, "unsupported_tool_call", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyAppServerCommand(string command)
    {
        return command.Contains("app-server", StringComparison.OrdinalIgnoreCase);
    }

    private static object BuildCapabilities(AgentRunRequest request)
    {
        return request.TrackerQuery is null
            ? new { }
            : new
            {
                tools = BuildAdvertisedTools(request)
            };
    }

    private static object[] BuildAdvertisedTools(AgentRunRequest request)
    {
        if (request.TrackerQuery is null)
        {
            return [];
        }

        return
        [
            new
            {
                name = "github_graphql",
                description = "Execute one GitHub GraphQL operation with the configured Symphony tracker credentials.",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string" },
                        variables = new { type = "object" }
                    },
                    required = new[] { "query" }
                }
            }
        ];
    }

    private static string BuildContinuationPrompt(AgentRunRequest request, int turnNumber)
    {
        return $"""
            Continue working on {request.IssueIdentifier}: {request.IssueTitle}.
            This is continuation turn {turnNumber} of {request.MaxTurns} in the current live Codex thread.
            Use the existing thread history instead of repeating the original issue prompt.
            If the issue is already complete or blocked, summarize the current status briefly and stop making changes.
            """;
    }

    private static async Task TryShutdownAppServerAsync(
        StreamWriter stdin,
        ProtocolReader protocolReader,
        Func<AgentRunUpdate, CancellationToken, Task>? onUpdate,
        int readTimeoutMs,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendRequestAsync(stdin, 4, "shutdown", new { }, cancellationToken);
            using var _ = await ReadResponseAsync(protocolReader, 4, onUpdate, readTimeoutMs, cancellationToken);
        }
        catch
        {
        }

        try
        {
            await SendNotificationAsync(stdin, "exit", null, cancellationToken);
            stdin.Close();
        }
        catch
        {
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
            "Process for issue_identifier={IssueIdentifier} outcome=forcing_termination context={Context}",
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
                "Process termination failed issue_identifier={IssueIdentifier} context={Context} error={Error}",
                issueIdentifier,
                context,
                SecretRedactor.Redact(killError));
        }

        if (!process.HasExited)
        {
            exitedAfterKillAttempt = await WaitForExitWithGracePeriodAsync(process, KillGracePeriodMs);
        }

        return new TerminationOutcome(exitedAfterKillAttempt, killError);
    }

    private static async Task SafeAwaitReaderPumpAsync(Task readerPumpTask, int maxWaitMs)
    {
        try
        {
            var completedTask = await Task.WhenAny(readerPumpTask, Task.Delay(maxWaitMs));
            if (completedTask == readerPumpTask)
            {
                await readerPumpTask;
            }
        }
        catch
        {
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

    private static async Task SendResponseAsync(
        StreamWriter writer,
        object requestId,
        object result,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = requestId,
            ["result"] = result
        });

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
        ProtocolReader protocolReader,
        int expectedRequestId,
        Func<AgentRunUpdate, CancellationToken, Task>? onUpdate,
        int readTimeoutMs,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCts.CancelAfter(readTimeoutMs);

            ProtocolLine protocolLine;
            try
            {
                protocolLine = await protocolReader.ReadNextAsync(readCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RunnerFailureException("response_timeout", $"Timed out waiting for response id {expectedRequestId}.");
            }

            if (protocolLine.IsEndOfStream)
            {
                throw new RunnerFailureException("subprocess_exit", $"App-server stdout closed before response id {expectedRequestId} was received.");
            }

            if (protocolLine.Document is null)
            {
                await ReportUpdateAsync(
                    onUpdate,
                    new AgentRunUpdate(
                        EventType: "malformed",
                        Timestamp: DateTimeOffset.UtcNow,
                        CodexAppServerPid: protocolReader.ProcessId,
                        Message: protocolLine.RawLine,
                        DataJson: JsonSerializer.Serialize(new { raw = SecretRedactor.Redact(protocolLine.RawLine, protocolReader.KnownSecret) })),
                    cancellationToken);
                continue;
            }

            if (TryGetResponseId(protocolLine.Document.RootElement, out var responseId) &&
                responseId is int responseAsInt &&
                responseAsInt == expectedRequestId)
            {
                return protocolLine.Document;
            }

            using (protocolLine.Document)
            {
                var eventName = GetEventName(protocolLine.Document.RootElement);
                await ReportUpdateAsync(
                    onUpdate,
                    CreateProtocolUpdate(protocolLine.Document.RootElement, eventName, null, null, protocolReader.KnownSecret),
                    cancellationToken);
            }
        }
    }

    private static AgentRunUpdate CreateProtocolUpdate(
        JsonElement message,
        string eventName,
        string? threadId,
        string? turnId,
        string? knownSecret)
    {
        TryExtractUsage(message, out var usage);
        TryExtractRateLimitsJson(message, out var rateLimitsJson);

        return new AgentRunUpdate(
            EventType: string.IsNullOrWhiteSpace(eventName) ? "other_message" : eventName,
            Timestamp: DateTimeOffset.UtcNow,
            ThreadId: threadId,
            TurnId: turnId,
            Message: SecretRedactor.Redact(ExtractMessage(message), knownSecret),
            InputTokens: usage?.InputTokens,
            OutputTokens: usage?.OutputTokens,
            TotalTokens: usage?.TotalTokens,
            RateLimitsJson: SecretRedactor.Redact(rateLimitsJson, knownSecret),
            DataJson: SecretRedactor.Redact(message.GetRawText(), knownSecret));
    }

    private static bool TryExtractUsage(JsonElement root, out UsageSnapshot? usage)
    {
        foreach (var propertyName in new[] { "total_token_usage", "totalTokenUsage", "token_usage", "tokenUsage", "usage" })
        {
            if (!TryFindPropertyRecursive(root, propertyName, out var usageElement))
            {
                continue;
            }

            if (TryParseUsageObject(usageElement, out usage))
            {
                return true;
            }
        }

        usage = null;
        return false;
    }

    private static bool TryExtractRateLimitsJson(JsonElement root, out string? rateLimitsJson)
    {
        foreach (var propertyName in new[] { "rate_limits", "rateLimits", "rate_limit", "rateLimit" })
        {
            if (TryFindPropertyRecursive(root, propertyName, out var rateLimitsElement))
            {
                rateLimitsJson = rateLimitsElement.GetRawText();
                return true;
            }
        }

        rateLimitsJson = null;
        return false;
    }

    private static bool TryFindPropertyRecursive(JsonElement element, string propertyName, out JsonElement found)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName))
                {
                    found = property.Value;
                    return true;
                }

                if (property.NameEquals("last_token_usage") || property.NameEquals("lastTokenUsage"))
                {
                    continue;
                }

                if (TryFindPropertyRecursive(property.Value, propertyName, out found))
                {
                    return true;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindPropertyRecursive(item, propertyName, out found))
                {
                    return true;
                }
            }
        }

        found = default;
        return false;
    }

    private static bool TryParseUsageObject(JsonElement usageElement, out UsageSnapshot? usage)
    {
        if (usageElement.ValueKind != JsonValueKind.Object)
        {
            usage = null;
            return false;
        }

        var inputTokens = GetOptionalInt32(usageElement, "input_tokens")
                          ?? GetOptionalInt32(usageElement, "inputTokens")
                          ?? GetOptionalInt32(usageElement, "prompt_tokens")
                          ?? GetOptionalInt32(usageElement, "promptTokens");
        var outputTokens = GetOptionalInt32(usageElement, "output_tokens")
                           ?? GetOptionalInt32(usageElement, "outputTokens")
                           ?? GetOptionalInt32(usageElement, "completion_tokens")
                           ?? GetOptionalInt32(usageElement, "completionTokens");
        var totalTokens = GetOptionalInt32(usageElement, "total_tokens")
                          ?? GetOptionalInt32(usageElement, "totalTokens");

        if (inputTokens is null && outputTokens is null && totalTokens is null)
        {
            usage = null;
            return false;
        }

        usage = new UsageSnapshot(inputTokens, outputTokens, totalTokens);
        return true;
    }

    private static int? GetOptionalInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
            _ => null
        };
    }

    private static string GetEventName(JsonElement message)
    {
        return GetOptionalString(message, "method")
               ?? GetOptionalString(message, "event")
               ?? GetOptionalString(message, "type")
               ?? "other_message";
    }

    private static bool IsTurnCompletedEvent(string eventName) =>
        eventName.Contains("turn/completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsTurnFailedEvent(string eventName) =>
        eventName.Contains("turn/failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsTurnCancelledEvent(string eventName) =>
        eventName.Contains("turn/cancelled", StringComparison.OrdinalIgnoreCase);

    private static bool IsUserInputRequired(JsonElement message, string eventName)
    {
        if (eventName.Contains("requestuserinput", StringComparison.OrdinalIgnoreCase) ||
            eventName.Contains("user_input", StringComparison.OrdinalIgnoreCase) ||
            eventName.Contains("input_required", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return HasBooleanPropertyRecursive(message, "requiresUserInput") ||
               HasBooleanPropertyRecursive(message, "inputRequired") ||
               HasBooleanPropertyRecursive(message, "userInputRequired");
    }

    private static bool HasBooleanPropertyRecursive(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName) &&
                    property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return property.Value.GetBoolean();
                }

                if (HasBooleanPropertyRecursive(property.Value, propertyName))
                {
                    return true;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasBooleanPropertyRecursive(item, propertyName))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryGetApprovalRequest(JsonElement message, string eventName, out object? requestId)
    {
        if (!eventName.Contains("approval", StringComparison.OrdinalIgnoreCase) ||
            eventName.Contains("approved", StringComparison.OrdinalIgnoreCase))
        {
            requestId = null;
            return false;
        }

        return TryGetResponseId(message, out requestId);
    }

    private static bool TryGetToolCall(JsonElement message, string eventName, out ToolCallRequest toolCall)
    {
        toolCall = new ToolCallRequest(string.Empty, string.Empty, null);

        if (!eventName.Contains("tool", StringComparison.OrdinalIgnoreCase) ||
            !eventName.Contains("call", StringComparison.OrdinalIgnoreCase) ||
            !TryGetResponseId(message, out var requestId))
        {
            return false;
        }

        var payload = message.TryGetProperty("params", out var paramsElement)
            ? paramsElement
            : message;

        string? toolName = GetOptionalString(payload, "toolName")
                           ?? GetOptionalString(payload, "tool_name")
                           ?? GetOptionalString(payload, "name");

        if (string.IsNullOrWhiteSpace(toolName) &&
            payload.TryGetProperty("tool", out var toolElement) &&
            toolElement.ValueKind == JsonValueKind.Object)
        {
            toolName = GetOptionalString(toolElement, "name");
        }

        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        string? inputJson = null;
        foreach (var propertyName in new[] { "input", "arguments", "args", "parameters" })
        {
            if (payload.TryGetProperty(propertyName, out var inputElement) &&
                inputElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                inputJson = inputElement.GetRawText();
                break;
            }
        }

        toolCall = new ToolCallRequest(requestId!, toolName, inputJson);
        return true;
    }

    private static string? ExtractMessage(JsonElement message)
    {
        foreach (var path in new[]
                 {
                     new[] { "message" },
                     new[] { "params", "message" },
                     new[] { "result", "message" },
                     new[] { "error", "message" },
                     new[] { "params", "item", "text" },
                     new[] { "result", "item", "text" }
                 })
        {
            var value = GetOptionalString(message, path);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryGetResponseId(JsonElement root, out object? responseId)
    {
        responseId = null;
        if (!root.TryGetProperty("id", out var idProperty))
        {
            return false;
        }

        if (idProperty.ValueKind == JsonValueKind.Number && idProperty.TryGetInt32(out var intId))
        {
            responseId = intId;
            return true;
        }

        if (idProperty.ValueKind == JsonValueKind.String)
        {
            responseId = idProperty.GetString();
            return !string.IsNullOrWhiteSpace(responseId as string);
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

        throw new RunnerFailureException("response_error", $"app_server_protocol_error:{operationName}:{errorProperty}");
    }

    private static string GetRequiredString(JsonElement root, params string[] path)
    {
        var value = GetOptionalString(root, path);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new RunnerFailureException("response_error", $"Missing required protocol field '{string.Join('.', path)}'.");
        }

        return value;
    }

    private static string? GetOptionalString(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out current) ||
                current.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
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
            startInfo.Arguments = $"/d /s /c {command}";
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
            stderr = AppendLine(stderr, $"Kill error: {SecretRedactor.Redact(termination.KillError)}");
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

    private static Task ReportUpdateAsync(
        Func<AgentRunUpdate, CancellationToken, Task>? onUpdate,
        AgentRunUpdate update,
        CancellationToken cancellationToken)
    {
        if (onUpdate is null)
        {
            return Task.CompletedTask;
        }

        return onUpdate(update, cancellationToken);
    }

    private readonly record struct TerminationOutcome(bool ExitedAfterKillAttempt, string KillError)
    {
        public static readonly TerminationOutcome NotNeeded = new(true, string.Empty);
    }

    private sealed class BoundedOutputBuffer(int maxChars, string streamName)
    {
        private readonly object sync = new();
        private readonly StringBuilder builder = new();
        private bool truncated;

        public void AppendLine(string line)
        {
            lock (sync)
            {
                if (truncated)
                {
                    return;
                }

                var remaining = maxChars - builder.Length;
                if (remaining <= 0)
                {
                    truncated = true;
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

                truncated = true;
            }
        }

        public string ToText()
        {
            lock (sync)
            {
                var text = builder.ToString().TrimEnd();
                if (!truncated)
                {
                    return text;
                }

                var suffix = $"[{streamName} truncated at {maxChars} chars]";
                return string.IsNullOrWhiteSpace(text)
                    ? suffix
                    : $"{text}{Environment.NewLine}{suffix}";
            }
        }
    }

    private sealed class ProtocolReader(
        StreamReader stdout,
        BoundedOutputBuffer stdoutBuffer,
        int processId,
        string? knownSecret)
    {
        public int ProcessId => processId;

        public string? KnownSecret => knownSecret;

        public async Task<ProtocolLine> ReadNextAsync(CancellationToken cancellationToken)
        {
            var line = await stdout.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return ProtocolLine.EndOfStream();
            }

            stdoutBuffer.AppendLine(line);

            try
            {
                return ProtocolLine.Json(line, JsonDocument.Parse(line));
            }
            catch (JsonException)
            {
                return ProtocolLine.Text(line);
            }
        }
    }

    private readonly record struct ProtocolLine(string? RawLine, JsonDocument? Document, bool IsEndOfStream)
    {
        public static ProtocolLine EndOfStream() => new(null, null, true);

        public static ProtocolLine Json(string rawLine, JsonDocument document) => new(rawLine, document, false);

        public static ProtocolLine Text(string rawLine) => new(rawLine, null, false);
    }

    private sealed class RunnerFailureException(string code, string message, Exception? innerException = null)
        : Exception(message, innerException)
    {
        public string Code { get; } = code;
    }

    private sealed record ToolCallRequest(object RequestId, string Name, string? InputJson);

    private sealed record UsageSnapshot(int? InputTokens, int? OutputTokens, int? TotalTokens);
}
