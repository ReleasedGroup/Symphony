using Microsoft.EntityFrameworkCore;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

public sealed partial class OrchestrationTickService
{
    private async Task RecoverOrphanedStateAsync(
        string instanceId,
        WorkflowDefinition workflowDefinition,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var orphanedRuns = await dbContext.Runs
            .Where(run =>
                (run.Status == RunStatusNames.Running || run.Status == RunStatusNames.Retrying) &&
                run.OwnerInstanceId != instanceId)
            .ToListAsync(cancellationToken);

        foreach (var run in orphanedRuns)
        {
            var claim = await dbContext.DispatchClaims.SingleOrDefaultAsync(
                entity => entity.IssueId == run.IssueId && entity.Status == "active",
                cancellationToken);

            if (claim is not null)
            {
                claim.ClaimedByInstanceId = instanceId;
                claim.UpdatedAtUtc = nowUtc;
            }

            if (run.Status == RunStatusNames.Retrying)
            {
                run.OwnerInstanceId = instanceId;
                var retry = await dbContext.RetryQueue.SingleOrDefaultAsync(
                    retryEntity => retryEntity.IssueId == run.IssueId,
                    cancellationToken);
                if (retry is not null)
                {
                    retry.OwnerInstanceId = instanceId;
                    retry.UpdatedAtUtc = nowUtc;
                }

                continue;
            }

            var activeAttempt = await dbContext.RunAttempts
                .Where(attempt => attempt.RunId == run.Id && attempt.CompletedAtUtc == null)
                .ToListAsync(cancellationToken);

            var latestAttempt = activeAttempt
                .OrderByDescending(attempt => attempt.StartedAtUtc)
                .FirstOrDefault();

            if (latestAttempt is not null)
            {
                latestAttempt.Status = RunStatusNames.Failed;
                latestAttempt.Error = "recovered after instance takeover";
                latestAttempt.CompletedAtUtc = nowUtc;
            }

            run.OwnerInstanceId = instanceId;
            run.Status = RunStatusNames.Retrying;
            run.CurrentRetryAttempt = (run.CurrentRetryAttempt ?? 0) + 1;
            run.LastEvent = "orphaned_run_recovered";
            run.LastMessage = "Recovered after instance takeover.";
            run.LastEventAtUtc = nowUtc;

            UpsertRetryEntry(
                run,
                instanceId,
                run.CurrentRetryAttempt.Value,
                RetryDelayTypes.Backoff,
                "recovered after instance takeover",
                nowUtc.AddSeconds(1),
                workflowDefinition.Runtime.Agent.MaxRetryBackoffMs);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ReconcileRunningIssuesAsync(
        WorkflowDefinition workflowDefinition,
        string? apiKey,
        WorkflowLoadException? preflightError,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var runningIssues = await dbContext.Runs
            .Where(run => run.Status == RunStatusNames.Running)
            .ToListAsync(cancellationToken);

        if (runningIssues.Count == 0)
        {
            return;
        }

        await ReconcileStalledRunsAsync(runningIssues, workflowDefinition, instanceId, cancellationToken);

        if (preflightError is not null || string.IsNullOrWhiteSpace(apiKey))
        {
            return;
        }

        IReadOnlyList<IssueStateSnapshot> refreshedStates;
        try
        {
            refreshedStates = await trackerClient.FetchIssueStatesByIdsAsync(
                BuildTrackerQuery(workflowDefinition, apiKey),
                runningIssues.Select(run => run.IssueId).ToList(),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Running issue reconciliation failed; active runs will continue.");
            return;
        }

        var refreshedById = refreshedStates.ToDictionary(state => state.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var run in runningIssues)
        {
            if (!refreshedById.TryGetValue(run.IssueId, out var refreshedState))
            {
                continue;
            }

            run.State = refreshedState.State;

            if (MatchesTerminalState(refreshedState.State, workflowDefinition.Runtime.Tracker.TerminalStates))
            {
                await RequestRunStopAsync(
                    run,
                    RunStopReasons.Terminal,
                    cleanupWorkspace: true,
                    workflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                    instanceId,
                    cancellationToken);
                continue;
            }

            if (!IssueStateMatcher.MatchesConfiguredActiveState(refreshedState.State, workflowDefinition.Runtime.Tracker.ActiveStates))
            {
                await RequestRunStopAsync(
                    run,
                    RunStopReasons.Inactive,
                    cleanupWorkspace: false,
                    workflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                    instanceId,
                    cancellationToken);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task ReconcileStalledRunsAsync(
        IReadOnlyList<RunEntity> runningIssues,
        WorkflowDefinition workflowDefinition,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var stallTimeoutMs = workflowDefinition.Runtime.Codex.StallTimeoutMs;
        if (stallTimeoutMs <= 0)
        {
            return;
        }

        var nowUtc = timeProvider.GetUtcNow();
        foreach (var run in runningIssues.Where(run => run.OwnerInstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase)))
        {
            var lastActivity = run.LastEventAtUtc ?? run.StartedAtUtc;
            if ((nowUtc - lastActivity).TotalMilliseconds <= stallTimeoutMs)
            {
                continue;
            }

            await RequestRunStopAsync(
                run,
                RunStopReasons.Stalled,
                cleanupWorkspace: false,
                workflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                instanceId,
                cancellationToken);
        }
    }

    private async Task RequestRunStopAsync(
        RunEntity run,
        string stopReason,
        bool cleanupWorkspace,
        int maxRetryBackoffMs,
        string instanceId,
        CancellationToken cancellationToken)
    {
        run.RequestedStopReason = stopReason;
        run.CleanupWorkspaceOnStop = cleanupWorkspace;
        run.LastEvent = "stop_requested";
        run.LastMessage = stopReason;
        run.LastEventAtUtc = timeProvider.GetUtcNow();

        var stopRequested = await issueExecutionCoordinator.TryStopAsync(run.IssueId, cancellationToken);
        if (stopRequested)
        {
            return;
        }

        var nowUtc = timeProvider.GetUtcNow();
        var activeAttempt = await dbContext.RunAttempts
            .Where(attempt => attempt.RunId == run.Id && attempt.CompletedAtUtc == null)
            .ToListAsync(cancellationToken);

        var latestAttempt = activeAttempt
            .OrderByDescending(attempt => attempt.StartedAtUtc)
            .FirstOrDefault();

        if (latestAttempt is not null)
        {
            latestAttempt.Status = stopReason == RunStopReasons.Stalled
                ? RunStatusNames.Stalled
                : RunStatusNames.CanceledByReconciliation;
            latestAttempt.Error = stopReason;
            latestAttempt.CompletedAtUtc = nowUtc;
        }

        if (stopReason == RunStopReasons.Stalled)
        {
            run.Status = RunStatusNames.Retrying;
            run.CurrentRetryAttempt = (run.CurrentRetryAttempt ?? 0) + 1;
            UpsertRetryEntry(
                run,
                instanceId,
                run.CurrentRetryAttempt.Value,
                RetryDelayTypes.Backoff,
                "stall timeout exceeded",
                nowUtc.AddMilliseconds(ComputeBackoffMs(run.CurrentRetryAttempt.Value, maxRetryBackoffMs)),
                maxRetryBackoffMs);
        }
        else
        {
            run.Status = RunStatusNames.CanceledByReconciliation;
            run.CompletedAtUtc = nowUtc;

            var retryEntry = await dbContext.RetryQueue.SingleOrDefaultAsync(
                retry => retry.IssueId == run.IssueId,
                cancellationToken);
            if (retryEntry is not null)
            {
                dbContext.RetryQueue.Remove(retryEntry);
            }

            await coordinationStore.ReleaseIssueClaimAsync(
                run.IssueId,
                instanceId,
                RunStatusNames.CanceledByReconciliation,
                cancellationToken);

            if (cleanupWorkspace)
            {
                await CleanupWorkspaceWithoutLiveRunAsync(run, cancellationToken);
            }
        }
    }

    private void UpsertRetryEntry(
        RunEntity run,
        string instanceId,
        int attempt,
        string delayType,
        string error,
        DateTimeOffset dueAtUtc,
        int maxRetryBackoffMs)
    {
        var retryEntry = dbContext.RetryQueue.SingleOrDefault(entry => entry.IssueId == run.IssueId);
        if (retryEntry is null)
        {
            dbContext.RetryQueue.Add(new RetryQueueEntity
            {
                IssueId = run.IssueId,
                IssueIdentifier = run.IssueIdentifier,
                RunId = run.Id,
                OwnerInstanceId = instanceId,
                Attempt = attempt,
                DueAtUtc = dueAtUtc,
                DelayType = delayType,
                Error = error,
                MaxBackoffMs = maxRetryBackoffMs,
                CreatedAtUtc = timeProvider.GetUtcNow(),
                UpdatedAtUtc = timeProvider.GetUtcNow()
            });
        }
        else
        {
            retryEntry.OwnerInstanceId = instanceId;
            retryEntry.Attempt = attempt;
            retryEntry.DueAtUtc = dueAtUtc;
            retryEntry.DelayType = delayType;
            retryEntry.Error = error;
            retryEntry.MaxBackoffMs = maxRetryBackoffMs;
            retryEntry.UpdatedAtUtc = timeProvider.GetUtcNow();
        }
    }

    private async Task CleanupWorkspaceWithoutLiveRunAsync(RunEntity run, CancellationToken cancellationToken)
    {
        var workflowDefinition = await workflowDefinitionProvider.GetCurrentAsync(cancellationToken);
        try
        {
            await workspaceManager.CleanupIssueWorkspaceAsync(
                new WorkspaceCleanupRequest(
                    run.IssueIdentifier,
                    workflowDefinition.Runtime.Workspace.Root,
                    workflowDefinition.Runtime.Workspace.SharedClonePath,
                    workflowDefinition.Runtime.Workspace.WorktreesRoot,
                    workflowDefinition.Runtime.Hooks.BeforeRemove,
                    workflowDefinition.Runtime.Hooks.TimeoutMs),
                cancellationToken);

            await UpdateWorkspaceCleanupRecordAsync(
                new NormalizedIssue(
                    run.IssueId,
                    run.IssueIdentifier,
                    run.IssueIdentifier,
                    null,
                    null,
                    run.State,
                    null,
                    null,
                    null,
                    [],
                    [],
                    [],
                    null,
                    null),
                RunStopReasons.Terminal,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Terminal cleanup failed for issue {IssueIdentifier}.", run.IssueIdentifier);
        }
    }
}
