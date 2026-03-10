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
        CancellationToken cancellationToken)
    {
        var terminalStates = workflowDefinition.Runtime.Tracker.TerminalStates;
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

        foreach (var issue in terminalIssues)
        {
            try
            {
                var cleanupResult = await workspaceManager.CleanupIssueWorkspaceAsync(
                    new WorkspaceCleanupRequest(
                        issue.Identifier,
                        workflowDefinition.Runtime.Workspace.Root,
                        workflowDefinition.Runtime.Workspace.SharedClonePath,
                        workflowDefinition.Runtime.Workspace.WorktreesRoot,
                        workflowDefinition.Runtime.Hooks.BeforeRemove,
                        workflowDefinition.Runtime.Hooks.TimeoutMs),
                    cancellationToken);

                if (cleanupResult.RemovedNow)
                {
                    await UpdateWorkspaceCleanupRecordAsync(issue, RunStopReasons.Terminal, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Startup terminal cleanup failed for issue {IssueIdentifier}.", issue.Identifier);
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

    private async Task UpdateWorkspaceCleanupRecordAsync(
        NormalizedIssue issue,
        string reason,
        CancellationToken cancellationToken)
    {
        var record = await dbContext.WorkspaceRecords.SingleOrDefaultAsync(
            entity => entity.IssueId == issue.Id,
            cancellationToken);

        if (record is null)
        {
            return;
        }

        record.LastCleanedAtUtc = timeProvider.GetUtcNow();
        record.LastCleanupReason = reason;
        await dbContext.SaveChangesAsync(cancellationToken);
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
