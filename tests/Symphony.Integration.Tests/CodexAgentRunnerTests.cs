using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;
using Symphony.Infrastructure.Agent.Codex;

namespace Symphony.Integration.Tests;

public sealed class CodexAgentRunnerTests
{
    [Fact]
    public async Task RunIssueAsync_ShouldUsePropertyParameterNamesForValidationErrors()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-runner"));
        var runner = CreateRunner();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunIssueAsync(CreateRequest("id-0", "#0", workspace.FullName, string.Empty, 30_000)));

        Assert.Equal("Command", ex.ParamName);
    }

    [Fact]
    public async Task RunIssueAsync_ShouldExecuteCommandInWorkspace()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-runner"));
        var runner = CreateRunner();

        var result = await runner.RunIssueAsync(CreateRequest("id-1", "#1", workspace.FullName, EchoCommand(), 30_000));

        Assert.True(result.Success, result.Stderr);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("symphony-runner-ok", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunIssueAsync_ShouldFailWhenCommandTimesOut()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-runner"));
        var runner = CreateRunner();

        var result = await runner.RunIssueAsync(CreateRequest("id-2", "#2", workspace.FullName, SleepCommand(), 200));

        Assert.False(result.Success);
        Assert.Equal("turn_timeout", result.ErrorCode);
        Assert.Contains("timed out", result.Stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunIssueAsync_ShouldCapCapturedOutput()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var workspace = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-runner"));
        var runner = CreateRunner();

        var result = await runner.RunIssueAsync(CreateRequest("id-3", "#3", workspace.FullName, LargeOutputCommand(), 30_000));

        Assert.True(result.Success);
        Assert.Contains("truncated", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunIssueAsync_ShouldCompleteTurnWhenAppServerEmitsCompletionEvent()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var updates = new List<AgentRunUpdate>();
        var runner = CreateRunner();
        using var harness = CreateAppServerHarness(StandardCompletionScript());

        var result = await runner.RunIssueAsync(
            CreateRequest("id-4", "#4", harness.WorkspacePath, harness.Command, 30_000),
            (update, _) =>
            {
                updates.Add(update);
                return Task.CompletedTask;
            });

        Assert.True(result.Success, result.Stderr);
        Assert.Contains("\"turn/completed\"", result.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(updates, update => update.EventType == "turn_completed");
    }

    [Fact]
    public async Task RunIssueAsync_ShouldReuseThreadAcrossContinuationTurnsUntilIssueStopsBeingActive()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var tracker = new SequencedTrackerClient(["Open", "Closed"]);
        var runner = CreateRunner(tracker);
        using var harness = CreateAppServerHarness(StandardCompletionScript());

        var result = await runner.RunIssueAsync(
            CreateRequest(
                "id-5",
                "#5",
                harness.WorkspacePath,
                harness.Command,
                30_000,
                maxTurns: 3,
                trackerQuery: CreateTrackerQuery()));

        Assert.True(result.Success, result.Stderr);
        Assert.Equal(2, tracker.RefreshCount);
        Assert.Equal(2, CountOccurrences(result.Stdout, "\"turn/completed\""));
    }

    [Fact]
    public async Task RunIssueAsync_ShouldAutoApproveSupportedRequestsAndExecuteGithubGraphQlTool()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var tracker = new SequencedTrackerClient(["Closed"]);
        var updates = new List<AgentRunUpdate>();
        var runner = CreateRunner(tracker);
        using var harness = CreateAppServerHarness(ApprovalAndToolScript());

        var result = await runner.RunIssueAsync(
            CreateRequest(
                "id-6",
                "#6",
                harness.WorkspacePath,
                harness.Command,
                30_000,
                maxTurns: 1,
                trackerQuery: CreateTrackerQuery()),
            (update, _) =>
            {
                updates.Add(update);
                return Task.CompletedTask;
            });

        Assert.True(result.Success, result.Stderr);
        Assert.Contains(updates, update => update.EventType == "approval_auto_approved");
        Assert.Contains(updates, update => update.EventType == "tool_call_succeeded");
        Assert.Contains(updates, update => !string.IsNullOrWhiteSpace(update.RateLimitsJson));
    }

    [Fact]
    public async Task RunIssueAsync_ShouldClassifySupportedToolFailuresAsToolCallFailed()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var tracker = new SequencedTrackerClient(
            ["Closed"],
            new GitHubGraphQlExecutionResult(
                false,
                "{\"errors\":[{\"message\":\"boom\"}]}",
                "github_graphql_errors",
                "GraphQL request failed."));
        var updates = new List<AgentRunUpdate>();
        var runner = CreateRunner(tracker);
        using var harness = CreateAppServerHarness(ToolFailureScript());

        var result = await runner.RunIssueAsync(
            CreateRequest(
                "id-6b",
                "#6b",
                harness.WorkspacePath,
                harness.Command,
                30_000,
                maxTurns: 1,
                trackerQuery: CreateTrackerQuery()),
            (update, _) =>
            {
                updates.Add(update);
                return Task.CompletedTask;
            });

        Assert.True(result.Success, result.Stderr);
        Assert.Contains(updates, update => update.EventType == "tool_call_failed");
        Assert.DoesNotContain(updates, update => update.EventType == "unsupported_tool_call");
    }

    [Fact]
    public async Task RunIssueAsync_ShouldFailWhenTurnRequestsUserInput()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var runner = CreateRunner(new SequencedTrackerClient(["Open"]));
        using var harness = CreateAppServerHarness(UserInputScript());

        var result = await runner.RunIssueAsync(
            CreateRequest(
                "id-7",
                "#7",
                harness.WorkspacePath,
                harness.Command,
                30_000,
                trackerQuery: CreateTrackerQuery()));

        Assert.False(result.Success);
        Assert.Equal("turn_input_required", result.ErrorCode);
    }

    [Fact]
    public async Task RunIssueAsync_ShouldBufferPartialStdoutLinesUntilTheyComplete()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var runner = CreateRunner(new SequencedTrackerClient(["Closed"]));
        using var harness = CreateAppServerHarness(PartialLineCompletionScript());

        var result = await runner.RunIssueAsync(
            CreateRequest(
                "id-8",
                "#8",
                harness.WorkspacePath,
                harness.Command,
                30_000,
                trackerQuery: CreateTrackerQuery()));

        Assert.True(result.Success, result.Stderr);
        Assert.Contains("\"turn/completed\"", result.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    private static CodexAgentRunner CreateRunner(ITrackerClient? trackerClient = null)
    {
        return new CodexAgentRunner(trackerClient ?? new SequencedTrackerClient([]), NullLogger<CodexAgentRunner>.Instance);
    }

    private static AgentRunRequest CreateRequest(
        string issueId,
        string issueIdentifier,
        string workspacePath,
        string command,
        int timeoutMs,
        int maxTurns = 1,
        TrackerQuery? trackerQuery = null)
    {
        return new AgentRunRequest(
            IssueId: issueId,
            IssueIdentifier: issueIdentifier,
            IssueTitle: $"Issue {issueIdentifier}",
            WorkspacePath: workspacePath,
            Prompt: "test prompt",
            Command: command,
            TimeoutMs: timeoutMs,
            MaxTurns: maxTurns,
            ApprovalPolicy: "never",
            ThreadSandbox: "danger-full-access",
            TurnSandboxPolicy: "danger-full-access",
            ReadTimeoutMs: 5_000,
            TrackerQuery: trackerQuery);
    }

    private static TrackerQuery CreateTrackerQuery()
    {
        return new TrackerQuery(
            Endpoint: "https://api.github.com/graphql",
            ApiKey: "test-token",
            Owner: "released",
            Repo: "symphony",
            ActiveStates: ["Open"],
            Labels: [],
            Milestone: null);
    }

    private static string EchoCommand() => "echo symphony-runner-ok";

    private static string SleepCommand()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "ping 127.0.0.1 -n 6 > nul"
            : "sleep 5";
    }

    private static string LargeOutputCommand() => "for /L %i in (1,1,40000) do @echo 0123456789";

    private static AppServerHarness CreateAppServerHarness(string script)
    {
        var workspacePath = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-runner")).FullName;
        var scriptPath = Path.Combine(workspacePath, "fake-app-server.ps1");
        var wrapperPath = Path.Combine(workspacePath, "fake-app-server-wrapper.cmd");
        File.WriteAllText(scriptPath, script);
        File.WriteAllText(wrapperPath, "@echo off\r\npowershell -NoProfile -ExecutionPolicy Bypass -File \"%~1\"\r\n");
        return new AppServerHarness(workspacePath, $"call \"{wrapperPath}\" \"{scriptPath}\"");
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string StandardCompletionScript() => """
        $turnIndex = 0
        while (($line = [Console]::In.ReadLine()) -ne $null) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $request = $line | ConvertFrom-Json
            if ($request.method -eq 'initialize') { @{ id = $request.id; result = @{ serverInfo = @{ name = 'fake'; version = '1.0' } } } | ConvertTo-Json -Compress; continue }
            if ($request.method -eq 'thread/start') { @{ id = $request.id; result = @{ thread = @{ id = 'thread-1' } } } | ConvertTo-Json -Compress; continue }
            if ($request.method -eq 'turn/start') {
                $turnIndex++
                @{ id = $request.id; result = @{ turn = @{ id = "turn-$turnIndex" } } } | ConvertTo-Json -Compress
                @{ method = 'turn/completed'; params = @{ message = "turn-$turnIndex done"; usage = @{ input_tokens = $turnIndex; output_tokens = $turnIndex; total_tokens = ($turnIndex * 2) } } } | ConvertTo-Json -Compress
                continue
            }
            if ($request.method -eq 'shutdown') { @{ id = $request.id; result = @{ ok = $true } } | ConvertTo-Json -Compress; break }
        }
        """;

    private static string ApprovalAndToolScript() => """
        while (($line = [Console]::In.ReadLine()) -ne $null) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $request = $line | ConvertFrom-Json
            if ($request.method -eq 'initialize') { @{ id = $request.id; result = @{ serverInfo = @{ name = 'fake'; version = '1.0' } } } | ConvertTo-Json -Compress; continue }
            if ($request.method -eq 'thread/start') { @{ id = $request.id; result = @{ thread = @{ id = 'thread-1' } } } | ConvertTo-Json -Compress; continue }
            if ($request.method -eq 'turn/start') {
                @{ id = $request.id; result = @{ turn = @{ id = 'turn-1' } } } | ConvertTo-Json -Compress
                @{ id = 'approval-1'; method = 'approval/request'; params = @{ kind = 'command' } } | ConvertTo-Json -Compress
                $approval = ([Console]::In.ReadLine() | ConvertFrom-Json)
                if (-not $approval.result.approved) { throw 'approval not granted' }
                @{ id = 'tool-1'; method = 'item/tool/call'; params = @{ name = 'github_graphql'; input = @{ query = 'query { viewer { login } }' } } } | ConvertTo-Json -Compress
                $tool = ([Console]::In.ReadLine() | ConvertFrom-Json)
                if (-not $tool.result.success) { throw 'tool call failed' }
                @{ method = 'notification'; params = @{ message = 'working'; usage = @{ input_tokens = 10; output_tokens = 4; total_tokens = 14 }; rate_limits = @{ remaining = 42 } } } | ConvertTo-Json -Compress
                @{ method = 'turn/completed'; params = @{ message = 'done' } } | ConvertTo-Json -Compress
                continue
            }
            if ($request.method -eq 'shutdown') { @{ id = $request.id; result = @{ ok = $true } } | ConvertTo-Json -Compress; break }
        }
        """;

    private static string UserInputScript() => """
        while (($line = [Console]::In.ReadLine()) -ne $null) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $request = $line | ConvertFrom-Json
            if ($request.method -eq 'initialize') { @{ id = $request.id; result = @{ serverInfo = @{ name = 'fake'; version = '1.0' } } } | ConvertTo-Json -Compress; continue }
            if ($request.method -eq 'thread/start') { @{ id = $request.id; result = @{ thread = @{ id = 'thread-1' } } } | ConvertTo-Json -Compress; continue }
            if ($request.method -eq 'turn/start') {
                @{ id = $request.id; result = @{ turn = @{ id = 'turn-1' } } } | ConvertTo-Json -Compress
                @{ id = 'input-1'; method = 'item/tool/requestUserInput'; params = @{ prompt = 'Need input' } } | ConvertTo-Json -Compress
                Start-Sleep -Milliseconds 500
            }
        }
        """;

    private static string PartialLineCompletionScript() => """
        while (($line = [Console]::In.ReadLine()) -ne $null) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $request = $line | ConvertFrom-Json
            if ($request.method -eq 'initialize') { @{ id = $request.id; result = @{ serverInfo = @{ name = 'fake'; version = '1.0' } } } | ConvertTo-Json -Compress; continue }
            if ($request.method -eq 'thread/start') { @{ id = $request.id; result = @{ thread = @{ id = 'thread-1' } } } | ConvertTo-Json -Compress; continue }
            if ($request.method -eq 'turn/start') {
                @{ id = $request.id; result = @{ turn = @{ id = 'turn-1' } } } | ConvertTo-Json -Compress
                $payload = (@{ method = 'turn/completed'; params = @{ message = 'partial-line' } } | ConvertTo-Json -Compress)
                [Console]::Out.Write($payload.Substring(0, 12))
                [Console]::Out.Flush()
                Start-Sleep -Milliseconds 100
                [Console]::Out.WriteLine($payload.Substring(12))
                [Console]::Out.Flush()
                continue
            }
            if ($request.method -eq 'shutdown') { @{ id = $request.id; result = @{ ok = $true } } | ConvertTo-Json -Compress; break }
        }
        """;

    private static string ToolFailureScript() => """
        while (($line = [Console]::In.ReadLine()) -ne $null) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            $request = $line | ConvertFrom-Json
            if ($request.method -eq 'initialize') { @{ id = $request.id; result = @{ serverInfo = @{ name = 'fake'; version = '1.0' } } } | ConvertTo-Json -Compress; continue }
            if ($request.method -eq 'thread/start') { @{ id = $request.id; result = @{ thread = @{ id = 'thread-1' } } } | ConvertTo-Json -Compress; continue }
            if ($request.method -eq 'turn/start') {
                @{ id = $request.id; result = @{ turn = @{ id = 'turn-1' } } } | ConvertTo-Json -Compress
                @{ id = 'tool-1'; method = 'item/tool/call'; params = @{ name = 'github_graphql'; input = @{ query = 'query { viewer { login } }' } } } | ConvertTo-Json -Compress
                $tool = ([Console]::In.ReadLine() | ConvertFrom-Json)
                if ($tool.result.success) { throw 'tool call unexpectedly succeeded' }
                if ($tool.result.error -ne 'github_graphql_errors') { throw "unexpected error code: $($tool.result.error)" }
                @{ method = 'turn/completed'; params = @{ message = 'tool failed as expected' } } | ConvertTo-Json -Compress
                continue
            }
            if ($request.method -eq 'shutdown') { @{ id = $request.id; result = @{ ok = $true } } | ConvertTo-Json -Compress; break }
        }
        """;

    private sealed class AppServerHarness(string workspacePath, string command) : IDisposable
    {
        public string WorkspacePath { get; } = workspacePath;

        public string Command { get; } = command;

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(WorkspacePath))
                {
                    Directory.Delete(WorkspacePath, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class SequencedTrackerClient(
        IReadOnlyList<string> states,
        GitHubGraphQlExecutionResult? graphQlResult = null) : ITrackerClient
    {
        private readonly Queue<string> pendingStates = new(states);
        private readonly GitHubGraphQlExecutionResult configuredGraphQlResult =
            graphQlResult ?? new GitHubGraphQlExecutionResult(true, "{\"data\":{\"viewer\":{\"login\":\"nick\"}}}");

        public int RefreshCount { get; private set; }

        public Task<IReadOnlyList<NormalizedIssue>> FetchCandidateIssuesAsync(TrackerQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NormalizedIssue>>([]);

        public Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByStatesAsync(TrackerQuery query, IReadOnlyList<string> states, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<NormalizedIssue>>([]);

        public Task<IReadOnlyList<IssueStateSnapshot>> FetchIssueStatesByIdsAsync(TrackerQuery query, IReadOnlyList<string> issueIds, CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            var nextState = pendingStates.Count == 0 ? "Closed" : pendingStates.Dequeue();
            return Task.FromResult<IReadOnlyList<IssueStateSnapshot>>([new IssueStateSnapshot(issueIds[0], nextState)]);
        }

        public Task<GitHubGraphQlExecutionResult> ExecuteGitHubGraphQlAsync(TrackerQuery query, string graphQlDocument, string? variablesJson, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(configuredGraphQlResult);
        }
    }
}
