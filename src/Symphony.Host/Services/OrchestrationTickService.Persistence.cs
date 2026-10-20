using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

public sealed partial class OrchestrationTickService
{
    private async Task RunStartupCleanupCoreAsync(
        WorkflowDefinition workflowDefinition,
        string apiKey,
        string instanceId,
        CancellationToken cancellationToken,
        bool onlyExistingWorkspaces = false)
    {
        var terminalStates = workflowDefinition.Runtime.Tracker.TerminalStates
            .Concat(workflowDefinition.Runtime.Tracker.ActiveStates).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (terminalStates.Count == 0)
        {
            logger.LogDebug("Skipping startup terminal cleanup because tracker.terminal_states is empty.");
            return;
        }

        var query = BuildTrackerQuery(workflowDefinition, apiKey);

        IReadOnlyList<NormalizedIssue> terminalIssues;
        try
        {
            terminalIssues = await trackerClient.FetchIssuesByStatesAsync(query, terminalStates, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Startup terminal cleanup could not fetch terminal issues for {Owner}/{Repo}. Continuing startup.",
                query.Owner,
                query.Repo);
            return;
        }

        var retainedIds = (await dbContext.IssueCache.Select(issue => issue.IssueId)
            .Union(dbContext.Runs.Select(run => run.IssueId))
            .Union(dbContext.RunAttempts.Select(attempt => attempt.IssueId))
            .Union(dbContext.WorkspaceRecords.Select(workspace => workspace.IssueId))
            .Union(dbContext.DispatchClaims.Select(claim => claim.IssueId))
            .Union(dbContext.RetryQueue.Select(retry => retry.IssueId))
            .Union(dbContext.EventLog.Where(entry => entry.IssueId != null).Select(entry => entry.IssueId!))
            .ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retainedIdentifiers = (await dbContext.EventLog.Where(entry => entry.IssueIdentifier != null)
            .Select(entry => entry.IssueIdentifier!).Distinct().ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var issue in terminalIssues)
        {
            var exists = Directory.Exists(Path.Combine(workflowDefinition.Runtime.Workspace.WorktreesRoot,
                WorkspacePathSafety.SanitizeIssueIdentifier(issue.Identifier)));
            if (!exists && (onlyExistingWorkspaces || (!retainedIds.Contains(issue.Id) && !retainedIdentifiers.Contains(issue.Identifier))))
            {
                continue;
            }
            // Persist discovery before removal, including worktrees left by a preparation crash.
            // All removals use the same reservation, liveness checks and fresh eligibility check.
            try
            {
                await UpsertIssueCacheAsync([issue], cancellationToken);
                await CleanupTerminalIssueAsync(issue.Id, issue.Identifier, workflowDefinition, instanceId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "workspace_cleanup_failed for {IssueId} {IssueIdentifier}; continuing reconciliation.", issue.Id, issue.Identifier);
            }
        }
    }

    private async Task PersistWorkflowSnapshotAsync(WorkflowDefinition workflowDefinition, CancellationToken cancellationToken)
    {
        var runtimeJson = JsonSerializer.Serialize(workflowDefinition.Runtime);
        var configHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runtimeJson)));

        var latestSnapshot = await dbContext.WorkflowSnapshots
            .OrderByDescending(snapshot => snapshot.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestSnapshot is not null &&
            latestSnapshot.ConfigHash == configHash &&
            latestSnapshot.SourcePath.Equals(workflowDefinition.SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        dbContext.WorkflowSnapshots.Add(new WorkflowSnapshotEntity
        {
            SourcePath = workflowDefinition.SourcePath,
            ConfigHash = configHash,
            RuntimeJson = runtimeJson,
            LoadedAtUtc = workflowDefinition.LoadedAtUtc
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task UpsertIssueCacheAsync(IReadOnlyList<NormalizedIssue> issues, CancellationToken cancellationToken)
    {
        var cachedAtUtc = timeProvider.GetUtcNow();
        foreach (var issue in issues)
        {
            var existing = await dbContext.IssueCache.SingleOrDefaultAsync(
                entity => entity.IssueId == issue.Id,
                cancellationToken);

            if (existing is null)
            {
                dbContext.IssueCache.Add(CreateIssueCacheEntity(issue, cachedAtUtc));
                continue;
            }

            existing.Identifier = issue.Identifier;
            existing.Title = issue.Title;
            existing.Description = issue.Description;
            existing.Priority = issue.Priority;
            existing.State = issue.State;
            existing.BranchName = issue.BranchName;
            existing.Url = issue.Url;
            existing.Milestone = issue.Milestone;
            existing.LabelsJson = JsonSerializer.Serialize(issue.Labels);
            existing.PullRequestsJson = JsonSerializer.Serialize(issue.PullRequests);
            existing.BlockedByJson = JsonSerializer.Serialize(issue.BlockedBy);
            existing.CreatedAtUtc = issue.CreatedAt;
            existing.UpdatedAtUtc = issue.UpdatedAt;
            existing.CachedAtUtc = cachedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task RefreshTrackedIssueCacheStatesAsync(
        WorkflowDefinition workflowDefinition,
        string apiKey,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var cachedIssues = await dbContext.IssueCache.ToListAsync(cancellationToken);
        // Include legacy/partial state without a cache row, including work left behind
        // while a live worker was finishing terminal reconciliation.
        var trackedIssues = await dbContext.IssueCache.Select(issue => new { issue.IssueId, issue.Identifier })
            .Union(dbContext.Runs.Select(run => new { run.IssueId, Identifier = run.IssueIdentifier }))
            .Union(dbContext.WorkspaceRecords.Select(record => new { record.IssueId, Identifier = record.IssueIdentifier }))
            .Union(dbContext.DispatchClaims.Select(claim => new { claim.IssueId, Identifier = claim.IssueIdentifier }))
            .Union(dbContext.RetryQueue.Select(retry => new { retry.IssueId, Identifier = retry.IssueIdentifier }))
            .Union(dbContext.RunAttempts.Select(attempt => new { attempt.IssueId, Identifier = (string)null! }))
            .Union(dbContext.EventLog.Where(entry => entry.IssueId != null)
                .Select(entry => new { IssueId = entry.IssueId!, Identifier = entry.IssueIdentifier! }))
            .ToListAsync(cancellationToken);
        if (trackedIssues.Count == 0)
        {
            return;
        }

        var refreshedStates = await TryFetchIssueStatesByIdsAsync(
            workflowDefinition,
            apiKey,
            trackedIssues.Select(issue => issue.IssueId).Distinct().ToList(),
            "Tracked issue cache state refresh failed; dashboard issue-state summaries may be stale.",
            cancellationToken);
        if (refreshedStates is null)
        {
            return;
        }

        var refreshedById = refreshedStates.ToDictionary(state => state.Id, StringComparer.OrdinalIgnoreCase);
        var cachedById = cachedIssues.ToDictionary(issue => issue.IssueId, StringComparer.OrdinalIgnoreCase);
        var cleanupIssueIds = (await dbContext.DispatchClaims.Where(claim => claim.Status == "cleanup")
            .Select(claim => claim.IssueId).ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hasChanges = false;
        var refreshedAtUtc = timeProvider.GetUtcNow();

        foreach (var trackedGroup in trackedIssues.GroupBy(issue => issue.IssueId, StringComparer.OrdinalIgnoreCase))
        {
            var issueId = trackedGroup.Key;
            if (!refreshedById.TryGetValue(issueId, out var refreshedState))
            {
                continue;
            }

            var isTerminal = MatchesTerminalState(
                refreshedState.State,
                workflowDefinition.Runtime.Tracker.TerminalStates);

            cachedById.TryGetValue(issueId, out var cachedIssue);
            if (cachedIssue is not null && !string.Equals(cachedIssue.State, refreshedState.State, StringComparison.OrdinalIgnoreCase))
            {
                cachedIssue.State = refreshedState.State;
                cachedIssue.CachedAtUtc = refreshedAtUtc;
                hasChanges = true;
            }

            if (isTerminal || !refreshedState.IsExecutionEligible(workflowDefinition.Runtime.Tracker.ActiveStates))
            {
                var identifier = refreshedState.Identifier ?? trackedGroup
                    .Select(issue => issue.Identifier).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (string.IsNullOrWhiteSpace(identifier))
                {
                    // Never invent a workspace path from an opaque GitHub node ID.
                    logger.LogWarning("terminal_issue_cleanup_deferred: tracker identifier missing for {IssueId}.", issueId);
                    continue;
                }

                await CleanupTerminalIssueAsync(issueId, identifier, workflowDefinition, instanceId, cancellationToken);
            }
            else if (cleanupIssueIds.Contains(issueId) &&
                refreshedState.IsExecutionEligible(workflowDefinition.Runtime.Tracker.ActiveStates))
            {
                await ReleaseReopenedCleanupReservationAsync(issueId, instanceId, cancellationToken);
            }
        }

        if (hasChanges)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<bool> ReleaseTerminalRetryStateAsync(
        IssueCacheEntity cachedIssue,
        string instanceId,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var retryingRuns = await dbContext.Runs
            .Where(run => run.IssueId == cachedIssue.IssueId && run.Status == RunStatusNames.Retrying)
            .ToListAsync(cancellationToken);

        foreach (var run in retryingRuns)
        {
            run.Status = RunStatusNames.CanceledByReconciliation;
            run.CompletedAtUtc = nowUtc;
            run.RequestedStopReason = null;
            run.CleanupWorkspaceOnStop = false;
            run.LastEvent = "terminal_state_observed";
            run.LastMessage = RunStopReasons.Terminal;
            run.LastEventAtUtc = nowUtc;
        }

        var retryEntries = await dbContext.RetryQueue
            .Where(entry => entry.IssueId == cachedIssue.IssueId)
            .ToListAsync(cancellationToken);
        if (retryEntries.Count > 0)
        {
            dbContext.RetryQueue.RemoveRange(retryEntries);
        }

        if (retryingRuns.Count > 0 || retryEntries.Count > 0)
        {
            await coordinationStore.ReleaseIssueClaimAsync(
                cachedIssue.IssueId,
                instanceId,
                RunStatusNames.CanceledByReconciliation,
                cancellationToken);
        }

        if (retryingRuns.Count > 0 || retryEntries.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        return false;
    }

    private async Task<IReadOnlyList<IssueStateSnapshot>?> TryFetchIssueStatesByIdsAsync(
        WorkflowDefinition workflowDefinition,
        string apiKey,
        IReadOnlyList<string> issueIds,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        if (issueIds.Count == 0)
        {
            return [];
        }

        try
        {
            return await trackerClient.FetchIssueStatesByIdsAsync(
                BuildTrackerQuery(workflowDefinition, apiKey),
                issueIds,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{FailureMessage}", failureMessage);
            return null;
        }
    }

    private static IssueCacheEntity CreateIssueCacheEntity(NormalizedIssue issue, DateTimeOffset cachedAtUtc)
    {
        return new IssueCacheEntity
        {
            IssueId = issue.Id,
            Identifier = issue.Identifier,
            Title = issue.Title,
            Description = issue.Description,
            Priority = issue.Priority,
            State = issue.State,
            BranchName = issue.BranchName,
            Url = issue.Url,
            Milestone = issue.Milestone,
            LabelsJson = JsonSerializer.Serialize(issue.Labels),
            PullRequestsJson = JsonSerializer.Serialize(issue.PullRequests),
            BlockedByJson = JsonSerializer.Serialize(issue.BlockedBy),
            CreatedAtUtc = issue.CreatedAt,
            UpdatedAtUtc = issue.UpdatedAt,
            CachedAtUtc = cachedAtUtc
        };
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
}
