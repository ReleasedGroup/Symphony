using Microsoft.EntityFrameworkCore;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

public sealed partial class OrchestrationTickService
{
    private async Task CleanupTerminalIssueAsync(
        string issueId,
        string identifier,
        WorkflowDefinition workflow,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var preflightKey = Symphony.Infrastructure.Workflows.WorkflowDispatchPreflightValidator.ValidateAndResolveApiKey(workflow);
        var refreshed = await TryFetchIssueStatesByIdsAsync(workflow, preflightKey, [issueId],
            "workspace_cleanup_refresh_failed; retaining workspace.", cancellationToken);
        var observed = refreshed?.SingleOrDefault(item => item.Id == issueId);
        if (observed is null || (!MatchesTerminalState(observed.State, workflow.Runtime.Tracker.TerminalStates) &&
            observed.IsExecutionEligible(workflow.Runtime.Tracker.ActiveStates)))
        {
            return;
        }

        // Flush reconciliation changes before bulk deletion; do not let tracked rows
        // reappear on a later SaveChanges. Reserve this issue in a short transaction
        // so hooks and filesystem operations cannot block unrelated database writers.
        await dbContext.SaveChangesAsync(cancellationToken);
        if (!AllowsWorkspaceCleanup(workflow, observed.State, issueId))
        {
            await ReleaseReopenedCleanupReservationAsync(issueId, instanceId, cancellationToken);
            return;
        }
        await using (var reservation = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            if (await dbContext.Runs.AnyAsync(
                    run => run.IssueId == issueId && run.Status == RunStatusNames.Running, cancellationToken) ||
                await dbContext.HasLiveWorkerForIssueAsync(issueId, timeProvider.GetUtcNow(), cancellationToken))
            {
                return;
            }

            var foreignOwners = await dbContext.DispatchClaims
                .Where(claim => claim.IssueId == issueId && (claim.Status == "active" || claim.Status == "cleanup") &&
                    claim.ClaimedByInstanceId != instanceId)
                .Select(claim => claim.ClaimedByInstanceId)
                .Union(dbContext.Runs.Where(run => run.IssueId == issueId && run.Status == RunStatusNames.Retrying &&
                    run.OwnerInstanceId != instanceId).Select(run => run.OwnerInstanceId))
                .ToListAsync(cancellationToken);
            var ownerLeases = await dbContext.InstanceLeases
                .Where(lease => foreignOwners.Contains(lease.OwnerInstanceId))
                .ToListAsync(cancellationToken);
            if (ownerLeases.Any(lease => lease.ExpiresAtUtc > timeProvider.GetUtcNow()))
            {
                return;
            }

            // Retain canceled state on filesystem failure so the next tick retries cleanup
            // without redispatching a known terminal issue.
            await ReleaseTerminalRetryStateAsync(new IssueCacheEntity { IssueId = issueId, Identifier = identifier },
                instanceId, cancellationToken);
            var cleanupClaim = await dbContext.DispatchClaims.SingleOrDefaultAsync(
                claim => claim.IssueId == issueId && (claim.Status == "active" || claim.Status == "cleanup"), cancellationToken);
            if (cleanupClaim is null)
            {
                cleanupClaim = new DispatchClaimEntity
                {
                    IssueId = issueId, IssueIdentifier = identifier, ClaimedAtUtc = timeProvider.GetUtcNow()
                };
                dbContext.DispatchClaims.Add(cleanupClaim);
            }
            cleanupClaim.Status = "cleanup";
            cleanupClaim.ClaimedByInstanceId = instanceId;
            cleanupClaim.UpdatedAtUtc = timeProvider.GetUtcNow();
            cleanupClaim.ReleasedAtUtc = null;
            await dbContext.SaveChangesAsync(cancellationToken);
            await reservation.CommitAsync(cancellationToken);
        }

        try
        {
            // A stop request or queued removal can outlive a close/reopen or filter edit.
            // The reservation blocks dispatch while we refresh immediately before removal.
            var currentWorkflow = await workflowDefinitionProvider.GetCurrentAsync(cancellationToken);
            var apiKey = Symphony.Infrastructure.Workflows.WorkflowDispatchPreflightValidator.ValidateAndResolveApiKey(currentWorkflow);
            var states = await TryFetchIssueStatesByIdsAsync(currentWorkflow, apiKey, [issueId],
                "workspace_cleanup_refresh_failed; retaining workspace for retry.", cancellationToken);
            var state = states?.SingleOrDefault(item => item.Id == issueId);
            if (state is null)
            {
                return;
            }
            if (!MatchesTerminalState(state.State, currentWorkflow.Runtime.Tracker.TerminalStates) &&
                state.IsExecutionEligible(currentWorkflow.Runtime.Tracker.ActiveStates))
            {
                await ReleaseReopenedCleanupReservationAsync(issueId, instanceId, cancellationToken);
                return;
            }

            if (!AllowsWorkspaceCleanup(currentWorkflow, state.State, issueId))
            {
                await ReleaseReopenedCleanupReservationAsync(issueId, instanceId, cancellationToken);
                return;
            }

            // Do not apply a reservation to a different filesystem after a workflow edit.
            if (currentWorkflow.Runtime.Workspace != workflow.Runtime.Workspace)
            {
                logger.LogInformation("workspace_cleanup_deferred: workspace settings changed for {IssueId}.", issueId);
                return;
            }

            // A large reconciliation batch can outlive its original poll lease. Renew
            // before touching the filesystem, with room for bounded removal/teardown.
            if (!await coordinationStore.AcquireOrRenewLeaseAsync(ResolveLeaseName(), instanceId,
                    TimeSpan.FromSeconds(Math.Max(120, ResolveLeaseTtl().TotalSeconds)), cancellationToken) ||
                !await dbContext.DispatchClaims.AsNoTracking().AnyAsync(claim => claim.IssueId == issueId &&
                    claim.Status == "cleanup" && claim.ClaimedByInstanceId == instanceId, cancellationToken))
            {
                logger.LogWarning("workspace_cleanup_deferred: reservation or poll lease lost for {IssueId}.", issueId);
                return;
            }

            var result = await workspaceManager.CleanupIssueWorkspaceAsync(new WorkspaceCleanupRequest(
                identifier, workflow.Runtime.Workspace.Root, workflow.Runtime.Workspace.SharedClonePath,
                workflow.Runtime.Workspace.WorktreesRoot, currentWorkflow.Runtime.Hooks.BeforeRemove,
                currentWorkflow.Runtime.Hooks.TimeoutMs), cancellationToken);
            if (result.Existed && !result.RemovedNow)
            {
                logger.LogWarning("terminal_issue_cleanup_deferred: workspace remains for {IssueId} {IssueIdentifier}.",
                    issueId, identifier);
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "terminal_issue_cleanup_failed for {IssueId} {IssueIdentifier}; state retained for retry.",
                issueId, identifier);
            return;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (!await dbContext.DispatchClaims.AnyAsync(claim => claim.IssueId == issueId &&
                claim.Status == "cleanup" && claim.ClaimedByInstanceId == instanceId, cancellationToken) ||
            await dbContext.Runs.AnyAsync(run => run.IssueId == issueId && run.Status == RunStatusNames.Running, cancellationToken) ||
            await dbContext.HasLiveWorkerForIssueAsync(issueId, timeProvider.GetUtcNow(), cancellationToken))
        {
            return;
        }

        var runIds = dbContext.Runs.Where(run => run.IssueId == issueId).Select(run => run.Id);
        var attemptIds = dbContext.RunAttempts
            .Where(attempt => attempt.IssueId == issueId || runIds.Contains(attempt.RunId)).Select(attempt => attempt.Id);
        var sessionIds = dbContext.Sessions
            .Where(session => runIds.Contains(session.RunId) || attemptIds.Contains(session.RunAttemptId))
            .Select(session => session.Id);
        var events = dbContext.EventLog.Where(entry => entry.IssueId == issueId || entry.IssueIdentifier == identifier ||
            runIds.Contains(entry.RunId!) || attemptIds.Contains(entry.RunAttemptId!) || sessionIds.Contains(entry.SessionId!));

        var totals = await dbContext.RuntimeTotals.AsNoTracking().SingleAsync(cancellationToken);
        totals.InputTokens += await dbContext.Runs.Where(run => run.IssueId == issueId)
            .SumAsync(run => (long)run.InputTokens, cancellationToken);
        totals.OutputTokens += await dbContext.Runs.Where(run => run.IssueId == issueId)
            .SumAsync(run => (long)run.OutputTokens, cancellationToken);
        totals.TotalTokens += await dbContext.Runs.Where(run => run.IssueId == issueId)
            .SumAsync(run => (long)run.TotalTokens, cancellationToken);
        var attempts = await dbContext.RunAttempts.AsNoTracking()
            .Where(attempt => attemptIds.Contains(attempt.Id)).ToListAsync(cancellationToken);
        totals.SecondsRunning += attempts.Sum(attempt => RunAttemptRuntime.CalculateSeconds(
            attempt.StartedAtUtc, attempt.CompletedAtUtc, attempt.Status, timeProvider.GetUtcNow()));
        var rateLimits = await events.Where(entry => entry.EventName == "rate_limits_updated" && entry.DataJson != null)
            .OrderByDescending(entry => entry.Id).FirstOrDefaultAsync(cancellationToken);
        if (rateLimits is not null && rateLimits.Id > totals.RateLimitsEventId)
        {
            totals.RateLimitsEventId = rateLimits.Id;
            totals.RateLimitsJson = rateLimits.DataJson;
        }

        // Capture tracked references before the subqueries' parent rows are removed.
        var relatedRunIds = (await runIds.ToListAsync(cancellationToken)).ToHashSet();
        var relatedAttemptIds = attempts.Select(attempt => attempt.Id).ToHashSet();
        var relatedSessionIds = (await sessionIds.ToListAsync(cancellationToken)).ToHashSet();
        await dbContext.RuntimeTotals.ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.InputTokens, totals.InputTokens)
            .SetProperty(row => row.OutputTokens, totals.OutputTokens)
            .SetProperty(row => row.TotalTokens, totals.TotalTokens)
            .SetProperty(row => row.SecondsRunning, totals.SecondsRunning)
            .SetProperty(row => row.RateLimitsEventId, totals.RateLimitsEventId)
            .SetProperty(row => row.RateLimitsJson, totals.RateLimitsJson), cancellationToken);
        await events.ExecuteDeleteAsync(cancellationToken);
        await dbContext.Sessions.Where(session => sessionIds.Contains(session.Id)).ExecuteDeleteAsync(cancellationToken);
        await dbContext.RunAttempts.Where(attempt => attemptIds.Contains(attempt.Id)).ExecuteDeleteAsync(cancellationToken);
        await dbContext.RetryQueue.Where(entry => entry.IssueId == issueId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.DispatchClaims.Where(entry => entry.IssueId == issueId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.WorkspaceRecords.Where(entry => entry.IssueId == issueId).ExecuteDeleteAsync(cancellationToken);
        foreach (var relatedRunId in relatedRunIds)
        {
            var workerLeasePrefix = WorkerLeaseNames.ForRun(relatedRunId);
            await dbContext.InstanceLeases.Where(lease => lease.LeaseName.StartsWith(workerLeasePrefix))
                .ExecuteDeleteAsync(cancellationToken);
        }
        await dbContext.Runs.Where(run => run.IssueId == issueId).ExecuteDeleteAsync(cancellationToken);
        await dbContext.IssueCache.Where(entry => entry.IssueId == issueId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
        {
            var removed = entry.Entity switch
            {
                IssueCacheEntity issue => issue.IssueId == issueId,
                RunEntity run => run.IssueId == issueId,
                RunAttemptEntity attempt => relatedAttemptIds.Contains(attempt.Id),
                SessionEntity session => relatedSessionIds.Contains(session.Id),
                RetryQueueEntity retry => retry.IssueId == issueId,
                WorkspaceRecordEntity workspace => workspace.IssueId == issueId,
                DispatchClaimEntity claim => claim.IssueId == issueId,
                InstanceLeaseEntity lease => relatedRunIds.Any(runId => lease.LeaseName.StartsWith(WorkerLeaseNames.ForRun(runId), StringComparison.Ordinal)),
                EventLogEntity log => log.IssueId == issueId || log.IssueIdentifier == identifier ||
                    relatedRunIds.Contains(log.RunId ?? "") || relatedAttemptIds.Contains(log.RunAttemptId ?? "") ||
                    relatedSessionIds.Contains(log.SessionId ?? ""),
                _ => false
            };
            if (removed)
            {
                entry.State = EntityState.Detached;
            }
        }

        // Emit to structured operational logging, not the issue event table being purged.
        logger.LogInformation("terminal_issue_state_purged for {IssueId} {IssueIdentifier}. RunCount={RunCount}",
            issueId, identifier, relatedRunIds.Count);
    }

    private bool AllowsWorkspaceCleanup(WorkflowDefinition workflow, string state, string issueId)
    {
        var settings = workflow.Runtime.Workspace;
        var allowed = settings.CleanupMode == "ineligible" ||
            (settings.CleanupMode == "terminal" && MatchesTerminalState(state, workflow.Runtime.Tracker.TerminalStates));
        if (!allowed || settings.CleanupDryRun)
        {
            logger.LogInformation(
                "workspace_cleanup_policy: IssueId={IssueId} Mode={Mode} DryRun={DryRun} Candidate={Candidate}; workspace and history retained. Safety checks have not been evaluated.",
                issueId, settings.CleanupMode, settings.CleanupDryRun, allowed);
            return false;
        }
        return true;
    }

    private async Task ReleaseReopenedCleanupReservationAsync(string issueId, string instanceId, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var claims = await dbContext.DispatchClaims.Where(claim => claim.IssueId == issueId && claim.Status == "cleanup")
            .ToListAsync(cancellationToken);
        var foreignOwners = claims.Where(claim => claim.ClaimedByInstanceId != instanceId)
            .Select(claim => claim.ClaimedByInstanceId).ToList();
        var leases = await dbContext.InstanceLeases.Where(lease => foreignOwners.Contains(lease.OwnerInstanceId))
            .ToListAsync(cancellationToken);
        if (leases.Any(lease => lease.ExpiresAtUtc > timeProvider.GetUtcNow()))
        {
            return;
        }

        dbContext.DispatchClaims.RemoveRange(claims);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
