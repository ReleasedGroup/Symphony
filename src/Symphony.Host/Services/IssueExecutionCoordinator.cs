using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Workflows;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

public sealed class IssueExecutionCoordinator(
    IServiceScopeFactory serviceScopeFactory,
    IHostApplicationLifetime applicationLifetime,
    TimeProvider timeProvider,
    ILogger<IssueExecutionCoordinator> logger) : IIssueExecutionCoordinator
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeRuns = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> TryStartAsync(IssueExecutionRequest request, CancellationToken cancellationToken = default)
    {
        var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            applicationLifetime.ApplicationStopping,
            cancellationToken);

        if (!_activeRuns.TryAdd(request.Issue.Id, linkedSource))
        {
            linkedSource.Dispose();
            return Task.FromResult(false);
        }

        _ = Task.Run(() => ExecuteRunAsync(request, linkedSource), CancellationToken.None);
        return Task.FromResult(true);
    }

    public Task<bool> TryStopAsync(string issueId, CancellationToken cancellationToken = default)
    {
        if (!_activeRuns.TryGetValue(issueId, out var cancellationSource))
        {
            return Task.FromResult(false);
        }

        cancellationSource.Cancel();
        return Task.FromResult(true);
    }

    private async Task ExecuteRunAsync(IssueExecutionRequest request, CancellationTokenSource cancellationSource)
    {
        var cancellationToken = cancellationSource.Token;
        WorkspacePreparationResult? workspace = null;
        string? finalStatus = null;
        string? finalError = null;
        RetryPlan? retryPlan = null;
        bool releaseClaim = false;
        var releaseStatus = RunStatusNames.Failed;
        var cleanupWorkspace = false;

        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var workspaceManager = scope.ServiceProvider.GetRequiredService<IWorkspaceManager>();
            var workspaceHookRunner = scope.ServiceProvider.GetRequiredService<IWorkspaceHookRunner>();
            var workflowPromptRenderer = scope.ServiceProvider.GetRequiredService<IWorkflowPromptRenderer>();
            var agentRunner = scope.ServiceProvider.GetRequiredService<IAgentRunner>();
            var dbContext = scope.ServiceProvider.GetRequiredService<SymphonyDbContext>();

            await AppendEventAsync(
                dbContext,
                request,
                "dispatch_started",
                LogLevel.Information,
                $"Dispatch started for {request.Issue.Identifier}.",
                cancellationToken);

            var remoteUrl = ResolveRemoteUrl(
                request.WorkflowDefinition.Runtime.Workspace.RemoteUrl,
                request.WorkflowDefinition.Runtime.Tracker.Owner,
                request.WorkflowDefinition.Runtime.Tracker.Repo);

            workspace = await workspaceManager.PrepareIssueWorkspaceAsync(
                new WorkspacePreparationRequest(
                    IssueId: request.Issue.Id,
                    IssueIdentifier: request.Issue.Identifier,
                    SuggestedBranchName: request.Issue.BranchName,
                    WorkspaceRoot: request.WorkflowDefinition.Runtime.Workspace.Root,
                    SharedClonePath: request.WorkflowDefinition.Runtime.Workspace.SharedClonePath,
                    WorktreesRoot: request.WorkflowDefinition.Runtime.Workspace.WorktreesRoot,
                    BaseBranch: request.WorkflowDefinition.Runtime.Workspace.BaseBranch,
                    RemoteRepositoryUrl: remoteUrl),
                cancellationToken);

            await UpsertWorkspaceRecordAsync(
                dbContext,
                request,
                workspace,
                timeProvider.GetUtcNow(),
                cancellationToken);

            WorkspacePathSafety.EnsurePathIsWithinRoot(
                request.WorkflowDefinition.Runtime.Workspace.Root,
                workspace.WorkspacePath);

            if (!Directory.Exists(workspace.WorkspacePath))
            {
                throw new InvalidOperationException($"Workspace path does not exist: {workspace.WorkspacePath}");
            }

            if (workspace.CreatedNow)
            {
                await RunRequiredHookAsync(
                    workspaceHookRunner,
                    "after_create",
                    request.WorkflowDefinition.Runtime.Hooks.AfterCreate,
                    request.Issue.Identifier,
                    workspace.WorkspacePath,
                    request.WorkflowDefinition.Runtime.Hooks.TimeoutMs,
                    cancellationToken);
            }

            await RunRequiredHookAsync(
                workspaceHookRunner,
                "before_run",
                request.WorkflowDefinition.Runtime.Hooks.BeforeRun,
                request.Issue.Identifier,
                workspace.WorkspacePath,
                request.WorkflowDefinition.Runtime.Hooks.TimeoutMs,
                cancellationToken);

            var prompt = workflowPromptRenderer.RenderForIssue(
                request.WorkflowDefinition,
                request.Issue,
                request.Attempt);

            var trackerQuery = BuildTrackerQuery(
                request.WorkflowDefinition,
                WorkflowDispatchPreflightValidator.ValidateAndResolveApiKey(request.WorkflowDefinition));

            var result = await agentRunner.RunIssueAsync(
                new AgentRunRequest(
                    request.Issue.Id,
                    request.Issue.Identifier,
                    request.Issue.Title,
                    workspace.WorkspacePath,
                    prompt,
                    request.WorkflowDefinition.Runtime.Codex.Command,
                    request.WorkflowDefinition.Runtime.Codex.TurnTimeoutMs,
                    request.WorkflowDefinition.Runtime.Agent.MaxTurns,
                    request.WorkflowDefinition.Runtime.Codex.ApprovalPolicy,
                    request.WorkflowDefinition.Runtime.Codex.ThreadSandbox,
                    request.WorkflowDefinition.Runtime.Codex.TurnSandboxPolicy,
                    request.WorkflowDefinition.Runtime.Codex.ReadTimeoutMs,
                    trackerQuery),
                (update, token) => PersistAgentUpdateAsync(request, update, token),
                cancellationToken);

            if (result.Success)
            {
                finalStatus = RunStatusNames.Succeeded;
                retryPlan = new RetryPlan(
                    Attempt: 1,
                    DueAtUtc: timeProvider.GetUtcNow().AddMilliseconds(1_000),
                    DelayType: RetryDelayTypes.Continuation,
                    Error: null);
            }
            else
            {
                finalStatus = ClassifyFailureStatus(result);
                finalError = SecretRedactor.Redact(Truncate(result.Stderr, 2_000));
                retryPlan = CreateFailureRetryPlan(request.Attempt, finalError, request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs);
            }
        }
        catch (WorkflowLoadException ex)
        {
            finalStatus = RunStatusNames.Failed;
            finalError = SecretRedactor.Redact($"{ex.Code}: {ex.Message}");
            retryPlan = CreateFailureRetryPlan(request.Attempt, finalError, request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs);
        }
        catch (WorkspaceHookExecutionException ex)
        {
            finalStatus = RunStatusNames.Failed;
            finalError = SecretRedactor.Redact($"{ex.HookName}: {ex.Message}");
            retryPlan = CreateFailureRetryPlan(request.Attempt, finalError, request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var stopState = await ReadStopStateAsync(request, CancellationToken.None);
            switch (stopState.RequestedStopReason)
            {
                case RunStopReasons.Terminal:
                    finalStatus = RunStatusNames.CanceledByReconciliation;
                    finalError = "terminal state reached";
                    releaseClaim = true;
                    releaseStatus = RunStatusNames.CanceledByReconciliation;
                    cleanupWorkspace = stopState.CleanupWorkspaceOnStop;
                    break;
                case RunStopReasons.Inactive:
                    finalStatus = RunStatusNames.CanceledByReconciliation;
                    finalError = "issue is no longer active";
                    releaseClaim = true;
                    releaseStatus = RunStatusNames.CanceledByReconciliation;
                    cleanupWorkspace = false;
                    break;
                case RunStopReasons.Stalled:
                    finalStatus = RunStatusNames.Stalled;
                    finalError = "stall timeout exceeded";
                    retryPlan = CreateFailureRetryPlan(request.Attempt, finalError, request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs);
                    break;
                default:
                    finalStatus = RunStatusNames.Failed;
                    finalError = "run canceled";
                    retryPlan = CreateFailureRetryPlan(request.Attempt, finalError, request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs);
                    break;
            }
        }
        catch (Exception ex)
        {
            finalStatus = RunStatusNames.Failed;
            finalError = SecretRedactor.Redact(Truncate(ex.Message, 2_000));
            retryPlan = CreateFailureRetryPlan(request.Attempt, finalError, request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs);
        }
        finally
        {
            try
            {
                if (workspace is not null)
                {
                    await using var scope = serviceScopeFactory.CreateAsyncScope();
                    var workspaceHookRunner = scope.ServiceProvider.GetRequiredService<IWorkspaceHookRunner>();
                    var dbContext = scope.ServiceProvider.GetRequiredService<SymphonyDbContext>();
                    var workspaceManager = scope.ServiceProvider.GetRequiredService<IWorkspaceManager>();

                    await RunBestEffortHookAsync(
                        workspaceHookRunner,
                        "after_run",
                        request.WorkflowDefinition.Runtime.Hooks.AfterRun,
                        request.Issue.Identifier,
                        workspace.WorkspacePath,
                        request.WorkflowDefinition.Runtime.Hooks.TimeoutMs,
                        CancellationToken.None);

                    if (cleanupWorkspace)
                    {
                        await workspaceManager.CleanupIssueWorkspaceAsync(
                            new WorkspaceCleanupRequest(
                                request.Issue.Identifier,
                                request.WorkflowDefinition.Runtime.Workspace.Root,
                                request.WorkflowDefinition.Runtime.Workspace.SharedClonePath,
                                request.WorkflowDefinition.Runtime.Workspace.WorktreesRoot,
                                request.WorkflowDefinition.Runtime.Hooks.BeforeRemove,
                                request.WorkflowDefinition.Runtime.Hooks.TimeoutMs),
                            CancellationToken.None);

                        await UpdateWorkspaceCleanupAsync(
                            dbContext,
                            request.Issue,
                            RunStopReasons.Terminal,
                            timeProvider.GetUtcNow(),
                            CancellationToken.None);
                    }

                    if (finalStatus is not null)
                    {
                        await PersistFinalStateAsync(
                            scope.ServiceProvider,
                            dbContext,
                            request,
                            finalStatus,
                            finalError,
                            retryPlan,
                            releaseClaim,
                            releaseStatus,
                            CancellationToken.None);
                    }
                }
                else if (finalStatus is not null)
                {
                    await using var scope = serviceScopeFactory.CreateAsyncScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<SymphonyDbContext>();
                    await PersistFinalStateAsync(
                        scope.ServiceProvider,
                        dbContext,
                        request,
                        finalStatus,
                        finalError,
                        retryPlan,
                        releaseClaim,
                        releaseStatus,
                        CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed finalizing run state for issue {IssueIdentifier}.", request.Issue.Identifier);
            }

            _activeRuns.TryRemove(request.Issue.Id, out _);
            cancellationSource.Dispose();
        }
    }

    private async Task PersistAgentUpdateAsync(
        IssueExecutionRequest request,
        AgentRunUpdate update,
        CancellationToken cancellationToken)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SymphonyDbContext>();

        var run = await dbContext.Runs.SingleOrDefaultAsync(runEntity => runEntity.Id == request.RunId, cancellationToken);
        if (run is null)
        {
            return;
        }

        run.LastEvent = update.EventType;
        run.LastMessage = SecretRedactor.Redact(Truncate(update.Message, 500));
        run.LastEventAtUtc = update.Timestamp;
        run.SessionId = update.SessionId ?? run.SessionId;

        ApplyTokenTotals(run, update);

        if (string.Equals(update.EventType, "session_started", StringComparison.OrdinalIgnoreCase))
        {
            run.TurnCount += 1;
        }

        if (!string.IsNullOrWhiteSpace(update.SessionId))
        {
            var session = await dbContext.Sessions.SingleOrDefaultAsync(
                entity => entity.Id == update.SessionId,
                cancellationToken);

            if (session is null)
            {
                session = new SessionEntity
                {
                    Id = update.SessionId,
                    RunId = request.RunId,
                    RunAttemptId = request.AttemptId,
                    ThreadId = update.ThreadId,
                    TurnId = update.TurnId,
                    CodexAppServerPid = update.CodexAppServerPid?.ToString(),
                    LastCodexEvent = update.EventType,
                    LastCodexTimestamp = update.Timestamp,
                    LastCodexMessage = SecretRedactor.Redact(Truncate(update.Message, 500)),
                    CreatedAtUtc = update.Timestamp,
                    UpdatedAtUtc = update.Timestamp,
                    TurnCount = string.Equals(update.EventType, "session_started", StringComparison.OrdinalIgnoreCase) ? 1 : 0
                };

                dbContext.Sessions.Add(session);
            }
            else
            {
                session.ThreadId = update.ThreadId ?? session.ThreadId;
                session.TurnId = update.TurnId ?? session.TurnId;
                session.CodexAppServerPid = update.CodexAppServerPid?.ToString() ?? session.CodexAppServerPid;
                session.LastCodexEvent = update.EventType;
                session.LastCodexTimestamp = update.Timestamp;
                session.LastCodexMessage = SecretRedactor.Redact(Truncate(update.Message, 500));
                session.UpdatedAtUtc = update.Timestamp;

                if (string.Equals(update.EventType, "session_started", StringComparison.OrdinalIgnoreCase))
                {
                    session.TurnCount += 1;
                }
            }

            ApplyTokenTotals(session, update);
        }

        dbContext.EventLog.Add(CreateAgentEventLogEntry(request, update));
        if (!string.IsNullOrWhiteSpace(update.RateLimitsJson))
        {
            dbContext.EventLog.Add(new EventLogEntity
            {
                IssueId = request.Issue.Id,
                IssueIdentifier = request.Issue.Identifier,
                RunId = request.RunId,
                RunAttemptId = request.AttemptId,
                SessionId = update.SessionId,
                EventName = "rate_limits_updated",
                Level = LogLevel.Information.ToString(),
                Message = "Rate limits updated.",
                DataJson = update.RateLimitsJson,
                OccurredAtUtc = update.Timestamp
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<(string? RequestedStopReason, bool CleanupWorkspaceOnStop)> ReadStopStateAsync(
        IssueExecutionRequest request,
        CancellationToken cancellationToken)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SymphonyDbContext>();
        var run = await dbContext.Runs.SingleOrDefaultAsync(runEntity => runEntity.Id == request.RunId, cancellationToken);
        return run is null
            ? (null, false)
            : (run.RequestedStopReason, run.CleanupWorkspaceOnStop);
    }

    private async Task PersistFinalStateAsync(
        IServiceProvider serviceProvider,
        SymphonyDbContext dbContext,
        IssueExecutionRequest request,
        string finalStatus,
        string? finalError,
        RetryPlan? retryPlan,
        bool releaseClaim,
        string releaseStatus,
        CancellationToken cancellationToken)
    {
        var completedAtUtc = timeProvider.GetUtcNow();

        var run = await dbContext.Runs.SingleAsync(runEntity => runEntity.Id == request.RunId, cancellationToken);
        var attempt = await dbContext.RunAttempts.SingleAsync(attemptEntity => attemptEntity.Id == request.AttemptId, cancellationToken);

        attempt.Status = finalStatus;
        attempt.Error = finalError;
        attempt.CompletedAtUtc = completedAtUtc;

        run.LastEvent = finalStatus;
        run.LastMessage = finalError;
        run.LastEventAtUtc = completedAtUtc;
        run.RequestedStopReason = null;
        run.CleanupWorkspaceOnStop = false;

        if (retryPlan is not null)
        {
            run.Status = RunStatusNames.Retrying;
            run.CurrentRetryAttempt = retryPlan.Attempt;
            run.OwnerInstanceId = request.InstanceId;

            var existingRetry = await dbContext.RetryQueue.SingleOrDefaultAsync(
                retryEntity => retryEntity.IssueId == request.Issue.Id,
                cancellationToken);

            if (existingRetry is null)
            {
                dbContext.RetryQueue.Add(new RetryQueueEntity
                {
                    IssueId = request.Issue.Id,
                    IssueIdentifier = request.Issue.Identifier,
                    RunId = request.RunId,
                    OwnerInstanceId = request.InstanceId,
                    Attempt = retryPlan.Attempt,
                    DueAtUtc = retryPlan.DueAtUtc,
                    DelayType = retryPlan.DelayType,
                    Error = retryPlan.Error,
                    MaxBackoffMs = request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                    CreatedAtUtc = completedAtUtc,
                    UpdatedAtUtc = completedAtUtc
                });
            }
            else
            {
                existingRetry.OwnerInstanceId = request.InstanceId;
                existingRetry.Attempt = retryPlan.Attempt;
                existingRetry.DueAtUtc = retryPlan.DueAtUtc;
                existingRetry.DelayType = retryPlan.DelayType;
                existingRetry.Error = retryPlan.Error;
                existingRetry.MaxBackoffMs = request.WorkflowDefinition.Runtime.Agent.MaxRetryBackoffMs;
                existingRetry.UpdatedAtUtc = completedAtUtc;
            }
        }
        else
        {
            run.Status = finalStatus;
            run.CompletedAtUtc = completedAtUtc;
            var existingRetry = await dbContext.RetryQueue.SingleOrDefaultAsync(
                retryEntity => retryEntity.IssueId == request.Issue.Id,
                cancellationToken);
            if (existingRetry is not null)
            {
                dbContext.RetryQueue.Remove(existingRetry);
            }
        }

        await AppendEventAsync(
            dbContext,
            request,
            retryPlan is null ? "run_completed" : "retry_scheduled",
            retryPlan is null ? LogLevel.Information : LogLevel.Warning,
            retryPlan is null
                ? $"Run completed with status {finalStatus}."
                : $"Retry scheduled with attempt {retryPlan.Attempt} due at {retryPlan.DueAtUtc:O}.",
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        if (releaseClaim)
        {
            var coordinationStore = serviceProvider.GetRequiredService<IOrchestrationCoordinationStore>();
            await coordinationStore.ReleaseIssueClaimAsync(
                request.Issue.Id,
                request.InstanceId,
                releaseStatus,
                cancellationToken);
        }
    }

    private async Task AppendEventAsync(
        SymphonyDbContext dbContext,
        IssueExecutionRequest request,
        string eventName,
        LogLevel level,
        string message,
        CancellationToken cancellationToken)
    {
        dbContext.EventLog.Add(new EventLogEntity
        {
            IssueId = request.Issue.Id,
            IssueIdentifier = request.Issue.Identifier,
            RunId = request.RunId,
            RunAttemptId = request.AttemptId,
            EventName = eventName,
            Level = level.ToString(),
            Message = message,
            OccurredAtUtc = timeProvider.GetUtcNow()
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task UpsertWorkspaceRecordAsync(
        SymphonyDbContext dbContext,
        IssueExecutionRequest request,
        WorkspacePreparationResult workspace,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken)
    {
        var workspaceRecord = await dbContext.WorkspaceRecords.SingleOrDefaultAsync(
            record => record.IssueId == request.Issue.Id,
            cancellationToken);

        if (workspaceRecord is null)
        {
            dbContext.WorkspaceRecords.Add(new WorkspaceRecordEntity
            {
                IssueId = request.Issue.Id,
                IssueIdentifier = request.Issue.Identifier,
                WorkspacePath = workspace.WorkspacePath,
                BranchName = workspace.BranchName,
                LastPreparedAtUtc = recordedAtUtc
            });
        }
        else
        {
            workspaceRecord.IssueIdentifier = request.Issue.Identifier;
            workspaceRecord.WorkspacePath = workspace.WorkspacePath;
            workspaceRecord.BranchName = workspace.BranchName;
            workspaceRecord.LastPreparedAtUtc = recordedAtUtc;
        }

        var run = await dbContext.Runs.SingleAsync(runEntity => runEntity.Id == request.RunId, cancellationToken);
        run.WorkspacePath = workspace.WorkspacePath;

        var attempt = await dbContext.RunAttempts.SingleAsync(attemptEntity => attemptEntity.Id == request.AttemptId, cancellationToken);
        attempt.WorkspacePath = workspace.WorkspacePath;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task UpdateWorkspaceCleanupAsync(
        SymphonyDbContext dbContext,
        NormalizedIssue issue,
        string reason,
        DateTimeOffset cleanedAtUtc,
        CancellationToken cancellationToken)
    {
        var workspaceRecord = await dbContext.WorkspaceRecords.SingleOrDefaultAsync(
            record => record.IssueId == issue.Id,
            cancellationToken);

        if (workspaceRecord is null)
        {
            return;
        }

        workspaceRecord.LastCleanedAtUtc = cleanedAtUtc;
        workspaceRecord.LastCleanupReason = reason;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string ClassifyFailureStatus(AgentRunResult result)
    {
        return result.ExitCode == -1 && result.Stderr.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            ? RunStatusNames.TimedOut
            : RunStatusNames.Failed;
    }

    private RetryPlan CreateFailureRetryPlan(int? attempt, string? error, int maxRetryBackoffMs)
    {
        var nextAttempt = attempt.HasValue ? attempt.Value + 1 : 1;
        var exponent = Math.Max(nextAttempt - 1, 0);
        var delayMs = Math.Min(10_000 * (int)Math.Pow(2, exponent), maxRetryBackoffMs);
        return new RetryPlan(
            Attempt: nextAttempt,
            DueAtUtc: timeProvider.GetUtcNow().AddMilliseconds(delayMs),
            DelayType: RetryDelayTypes.Backoff,
            Error: error);
    }

    private static string ResolveRemoteUrl(string? configuredRemoteUrl, string owner, string repo)
    {
        if (!string.IsNullOrWhiteSpace(configuredRemoteUrl))
        {
            return configuredRemoteUrl;
        }

        return $"https://github.com/{owner}/{repo}.git";
    }

    private static TrackerQuery BuildTrackerQuery(WorkflowDefinition workflowDefinition, string apiKey)
    {
        return new TrackerQuery(
            workflowDefinition.Runtime.Tracker.Endpoint,
            apiKey,
            workflowDefinition.Runtime.Tracker.Owner,
            workflowDefinition.Runtime.Tracker.Repo,
            workflowDefinition.Runtime.Tracker.ActiveStates,
            workflowDefinition.Runtime.Tracker.Labels,
            workflowDefinition.Runtime.Tracker.Milestone,
            workflowDefinition.Runtime.Tracker.IncludePullRequests);
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return value.Length <= maxLength
            ? value
            : $"{value[..maxLength]}...";
    }

    private static void ApplyTokenTotals(RunEntity run, AgentRunUpdate update)
    {
        (run.InputTokens, run.LastReportedInputTokens) = AccumulateAbsoluteTotal(run.InputTokens, run.LastReportedInputTokens, update.InputTokens);
        (run.OutputTokens, run.LastReportedOutputTokens) = AccumulateAbsoluteTotal(run.OutputTokens, run.LastReportedOutputTokens, update.OutputTokens);
        (run.TotalTokens, run.LastReportedTotalTokens) = AccumulateAbsoluteTotal(run.TotalTokens, run.LastReportedTotalTokens, update.TotalTokens);
    }

    private static void ApplyTokenTotals(SessionEntity session, AgentRunUpdate update)
    {
        (session.CodexInputTokens, session.LastReportedInputTokens) = AccumulateAbsoluteTotal(session.CodexInputTokens, session.LastReportedInputTokens, update.InputTokens);
        (session.CodexOutputTokens, session.LastReportedOutputTokens) = AccumulateAbsoluteTotal(session.CodexOutputTokens, session.LastReportedOutputTokens, update.OutputTokens);
        (session.CodexTotalTokens, session.LastReportedTotalTokens) = AccumulateAbsoluteTotal(session.CodexTotalTokens, session.LastReportedTotalTokens, update.TotalTokens);
    }

    private static (int AccumulatedTotal, int LastReportedTotal) AccumulateAbsoluteTotal(
        int accumulatedTotal,
        int lastReportedTotal,
        int? nextObservedTotal)
    {
        if (!nextObservedTotal.HasValue)
        {
            return (accumulatedTotal, lastReportedTotal);
        }

        var normalizedObservedTotal = Math.Max(nextObservedTotal.Value, lastReportedTotal);
        accumulatedTotal += Math.Max(normalizedObservedTotal - lastReportedTotal, 0);
        return (accumulatedTotal, normalizedObservedTotal);
    }

    private static EventLogEntity CreateAgentEventLogEntry(IssueExecutionRequest request, AgentRunUpdate update)
    {
        var level = update.EventType switch
        {
            "turn_failed" or "turn_cancelled" or "turn_input_required" => LogLevel.Warning,
            "malformed" or "unsupported_tool_call" or "tool_call_failed" => LogLevel.Warning,
            _ => LogLevel.Information
        };

        var message = SecretRedactor.Redact(Truncate(update.Message, 500)) ?? update.EventType;
        var dataJson = update.DataJson;
        if (!string.IsNullOrWhiteSpace(update.RateLimitsJson))
        {
            dataJson = update.DataJson is null
                ? JsonSerializer.Serialize(new { rate_limits = JsonDocument.Parse(update.RateLimitsJson).RootElement.Clone() })
                : update.DataJson;
        }

        return new EventLogEntity
        {
            IssueId = request.Issue.Id,
            IssueIdentifier = request.Issue.Identifier,
            RunId = request.RunId,
            RunAttemptId = request.AttemptId,
            SessionId = update.SessionId,
            EventName = update.EventType,
            Level = level.ToString(),
            Message = message,
            DataJson = dataJson,
            OccurredAtUtc = update.Timestamp
        };
    }

    private static async Task RunRequiredHookAsync(
        IWorkspaceHookRunner workspaceHookRunner,
        string hookName,
        string? hookScript,
        string issueIdentifier,
        string workspacePath,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hookScript))
        {
            return;
        }

        await workspaceHookRunner.RunHookAsync(
            new WorkspaceHookRequest(
                HookName: hookName,
                Script: hookScript,
                WorkspacePath: workspacePath,
                TimeoutMs: timeoutMs,
                IssueIdentifier: issueIdentifier),
            cancellationToken);
    }

    private async Task RunBestEffortHookAsync(
        IWorkspaceHookRunner workspaceHookRunner,
        string hookName,
        string? hookScript,
        string issueIdentifier,
        string workspacePath,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hookScript))
        {
            return;
        }

        try
        {
            await workspaceHookRunner.RunHookAsync(
                new WorkspaceHookRequest(
                    HookName: hookName,
                    Script: hookScript,
                    WorkspacePath: workspacePath,
                    TimeoutMs: timeoutMs,
                    IssueIdentifier: issueIdentifier),
                cancellationToken);
        }
        catch (WorkspaceHookExecutionException ex)
        {
            logger.LogWarning(ex, "{HookName} hook failed for issue {IssueIdentifier}.", hookName, issueIdentifier);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{HookName} hook failed unexpectedly for issue {IssueIdentifier}.", hookName, issueIdentifier);
        }
    }

    private sealed record RetryPlan(
        int Attempt,
        DateTimeOffset DueAtUtc,
        string DelayType,
        string? Error);
}
