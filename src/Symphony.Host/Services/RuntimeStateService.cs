using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;

namespace Symphony.Host.Services;

public sealed class RuntimeStateService(
    SymphonyDbContext dbContext,
    TimeProvider timeProvider)
{
    public async Task<object> GetStateAsync(CancellationToken cancellationToken)
    {
        var generatedAt = timeProvider.GetUtcNow();
        var retiredTotals = await dbContext.RuntimeTotals.AsNoTracking().SingleAsync(cancellationToken);
        var runningRuns = (await dbContext.Runs
            .AsNoTracking()
            .Where(run => run.Status == RunStatusNames.Running)
            .ToListAsync(cancellationToken))
            .OrderBy(run => run.StartedAtUtc)
            .ToList();
        var retryEntries = (await dbContext.RetryQueue
            .AsNoTracking()
            .ToListAsync(cancellationToken))
            .OrderBy(entry => entry.DueAtUtc)
            .ToList();
        var attempts = await dbContext.RunAttempts
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var latestRateLimits = await dbContext.EventLog
            .AsNoTracking()
            .Where(entry => entry.EventName == "rate_limits_updated" && entry.DataJson != null)
            .OrderByDescending(entry => entry.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var latestRateLimitsJson = latestRateLimits is not null && latestRateLimits.Id > retiredTotals.RateLimitsEventId
            ? latestRateLimits.DataJson
            : retiredTotals.RateLimitsJson;
        var issueCacheEntries = (await dbContext.IssueCache
            .AsNoTracking()
            .ToListAsync(cancellationToken))
            .OrderByDescending(entry => entry.UpdatedAtUtc ?? entry.CachedAtUtc)
            .ToList();
        var recentActivity = await GetRecentVisibleEventLogEntriesAsync(
            dbContext.EventLog.AsNoTracking(),
            limit: 24,
            cancellationToken);
        var leases = (await dbContext.InstanceLeases
            .AsNoTracking()
            .ToListAsync(cancellationToken))
            .OrderBy(entry => entry.LeaseName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(entry => entry.UpdatedAtUtc)
            .ToList();
        var issueCacheById = issueCacheEntries.ToDictionary(entry => entry.IssueId);
        var runningIssueIds = runningRuns.Select(run => run.IssueId).ToHashSet(StringComparer.Ordinal);
        var retryIssueIds = retryEntries.Select(entry => entry.IssueId).ToHashSet(StringComparer.Ordinal);

        var secondsRunning = retiredTotals.SecondsRunning + attempts.Sum(attempt =>
            RunAttemptRuntime.CalculateSeconds(attempt.StartedAtUtc, attempt.CompletedAtUtc, attempt.Status, generatedAt));

        return new
        {
            generated_at = generatedAt,
            counts = new
            {
                running = runningRuns.Count,
                retrying = retryEntries.Count,
                tracked = issueCacheEntries.Count
            },
            running = runningRuns.Select(run =>
            {
                issueCacheById.TryGetValue(run.IssueId, out var cachedIssue);
                return new
                {
                    issue_id = run.IssueId,
                    issue_identifier = run.IssueIdentifier,
                    title = cachedIssue?.Title,
                    url = cachedIssue?.Url,
                    milestone = cachedIssue?.Milestone,
                    labels = ParseJsonValue(cachedIssue?.LabelsJson),
                    state = run.State,
                    session_id = run.SessionId,
                    turn_count = run.TurnCount,
                    last_event = run.LastEvent,
                    last_message = DashboardEventPresentation.GetVisibleMessage(run.LastEvent, run.LastMessage),
                    started_at = run.StartedAtUtc,
                    last_event_at = run.LastEventAtUtc,
                    tokens = new
                    {
                        input_tokens = run.InputTokens,
                        output_tokens = run.OutputTokens,
                        total_tokens = run.TotalTokens
                    }
                };
            }),
            retrying = retryEntries.Select(entry =>
            {
                issueCacheById.TryGetValue(entry.IssueId, out var cachedIssue);
                return new
                {
                    issue_id = entry.IssueId,
                    issue_identifier = entry.IssueIdentifier,
                    title = cachedIssue?.Title,
                    url = cachedIssue?.Url,
                    milestone = cachedIssue?.Milestone,
                    labels = ParseJsonValue(cachedIssue?.LabelsJson),
                    attempt = entry.Attempt,
                    due_at = entry.DueAtUtc,
                    error = entry.Error
                };
            }),
            tracked = new
            {
                total = issueCacheEntries.Count,
                by_state = issueCacheEntries
                    .GroupBy(entry => string.IsNullOrWhiteSpace(entry.State) ? "Unknown" : entry.State)
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new
                    {
                        state = group.Key,
                        count = group.Count()
                    }),
                recently_updated = issueCacheEntries
                    .Take(18)
                    .Select(entry => new
                    {
                        issue_id = entry.IssueId,
                        issue_identifier = entry.Identifier,
                        title = entry.Title,
                        state = entry.State,
                        status = retryIssueIds.Contains(entry.IssueId)
                            ? RunStatusNames.Retrying
                            : runningIssueIds.Contains(entry.IssueId)
                                ? RunStatusNames.Running
                                : "tracked",
                        milestone = entry.Milestone,
                        updated_at = entry.UpdatedAtUtc ?? entry.CachedAtUtc,
                        url = entry.Url,
                        labels = ParseJsonValue(entry.LabelsJson)
                    })
            },
            activity = recentActivity.Select(entry => new
            {
                at = entry.OccurredAtUtc,
                issue_id = entry.IssueId,
                issue_identifier = entry.IssueIdentifier,
                session_id = entry.SessionId,
                level = entry.Level,
                @event = entry.EventName,
                message = DashboardEventPresentation.GetVisibleMessage(entry.EventName, entry.Message)
            }),
            coordination = new
            {
                leases = leases.Select(entry => new
                {
                    lease_name = entry.LeaseName,
                    owner_instance_id = entry.OwnerInstanceId,
                    acquired_at = entry.AcquiredAtUtc,
                    updated_at = entry.UpdatedAtUtc,
                    expires_at = entry.ExpiresAtUtc,
                    is_expired = entry.ExpiresAtUtc <= generatedAt
                })
            },
            codex_totals = new
            {
                input_tokens = retiredTotals.InputTokens + runningRuns.Sum(run => (long)run.InputTokens) + await dbContext.Runs
                    .AsNoTracking()
                    .Where(run => run.Status != RunStatusNames.Running)
                    .SumAsync(run => (long)run.InputTokens, cancellationToken),
                output_tokens = retiredTotals.OutputTokens + runningRuns.Sum(run => (long)run.OutputTokens) + await dbContext.Runs
                    .AsNoTracking()
                    .Where(run => run.Status != RunStatusNames.Running)
                    .SumAsync(run => (long)run.OutputTokens, cancellationToken),
                total_tokens = retiredTotals.TotalTokens + runningRuns.Sum(run => (long)run.TotalTokens) + await dbContext.Runs
                    .AsNoTracking()
                    .Where(run => run.Status != RunStatusNames.Running)
                    .SumAsync(run => (long)run.TotalTokens, cancellationToken),
                seconds_running = Math.Round(secondsRunning, 3)
            },
            rate_limits = ParseJsonValue(latestRateLimitsJson)
        };
    }

    public async Task<(bool Found, object? Payload)> GetIssueStateAsync(
        string issueIdentifier,
        CancellationToken cancellationToken)
    {
        var latestRun = (await dbContext.Runs
            .AsNoTracking()
            .Where(run => run.IssueIdentifier == issueIdentifier)
            .ToListAsync(cancellationToken))
            .OrderByDescending(run => run.StartedAtUtc)
            .FirstOrDefault();
        var workspaceRecord = await dbContext.WorkspaceRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.IssueIdentifier == issueIdentifier, cancellationToken);
        var retryEntry = await dbContext.RetryQueue
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.IssueIdentifier == issueIdentifier, cancellationToken);
        var issueCache = await dbContext.IssueCache
            .AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.Identifier == issueIdentifier, cancellationToken);
        var recentEvents = await GetRecentVisibleEventLogEntriesAsync(
            dbContext.EventLog
                .AsNoTracking()
                .Where(entry => entry.IssueIdentifier == issueIdentifier),
            limit: 20,
            cancellationToken);

        if (latestRun is null && workspaceRecord is null && retryEntry is null && issueCache is null && recentEvents.Count == 0)
        {
            return (false, null);
        }

        var issueId = latestRun?.IssueId ?? workspaceRecord?.IssueId ?? retryEntry?.IssueId ?? issueCache?.IssueId;
        var attemptCount = issueId is null
            ? 0
            : await dbContext.RunAttempts.CountAsync(attempt => attempt.IssueId == issueId, cancellationToken);
        var lastError = issueId is null
            ? null
            : (await dbContext.RunAttempts
                .AsNoTracking()
                .Where(attempt => attempt.IssueId == issueId && attempt.Error != null)
                .ToListAsync(cancellationToken))
                .OrderByDescending(attempt => attempt.CompletedAtUtc ?? attempt.StartedAtUtc)
                .Select(attempt => attempt.Error)
                .FirstOrDefault();

        var payload = new
        {
            issue_identifier = issueIdentifier,
            issue_id = issueId,
            status = latestRun?.Status ?? (retryEntry is null ? "tracked" : RunStatusNames.Retrying),
            workspace = new
            {
                path = workspaceRecord?.WorkspacePath
            },
            attempts = new
            {
                restart_count = Math.Max(attemptCount - 1, 0),
                current_retry_attempt = latestRun?.CurrentRetryAttempt ?? retryEntry?.Attempt
            },
            running = latestRun is null
                ? null
                : new
                {
                    session_id = latestRun.SessionId,
                    turn_count = latestRun.TurnCount,
                    state = latestRun.State,
                    started_at = latestRun.StartedAtUtc,
                    last_event = latestRun.LastEvent,
                    last_message = DashboardEventPresentation.GetVisibleMessage(latestRun.LastEvent, latestRun.LastMessage),
                    last_event_at = latestRun.LastEventAtUtc,
                    tokens = new
                    {
                        input_tokens = latestRun.InputTokens,
                        output_tokens = latestRun.OutputTokens,
                        total_tokens = latestRun.TotalTokens
                    }
                },
            retry = retryEntry is null
                ? null
                : new
                {
                    attempt = retryEntry.Attempt,
                    due_at = retryEntry.DueAtUtc,
                    error = retryEntry.Error
                },
            logs = new
            {
                codex_session_logs = Array.Empty<object>()
            },
            recent_events = recentEvents
                .OrderBy(entry => entry.OccurredAtUtc)
                .Select(entry => new
                {
                    at = entry.OccurredAtUtc,
                    @event = entry.EventName,
                    message = DashboardEventPresentation.GetVisibleMessage(entry.EventName, entry.Message)
                }),
            last_error = lastError,
            tracked = new
            {
                title = issueCache?.Title,
                url = issueCache?.Url,
                priority = issueCache?.Priority,
                cache_state = issueCache?.State,
                milestone = issueCache?.Milestone,
                updated_at = issueCache?.UpdatedAtUtc ?? issueCache?.CachedAtUtc,
                labels = ParseJsonValue(issueCache?.LabelsJson),
                blocked_by = ParseJsonValue(issueCache?.BlockedByJson),
                pull_requests = ParseJsonValue(issueCache?.PullRequestsJson)
            }
        };

        return (true, payload);
    }

    private static object? ParseJsonValue(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<List<EventLogEntity>> GetRecentVisibleEventLogEntriesAsync(
        IQueryable<EventLogEntity> query,
        int limit,
        CancellationToken cancellationToken)
    {
        const int minimumBatchSize = 32;
        var batchSize = Math.Max(limit * 2, minimumBatchSize);
        var offset = 0;
        var visibleEntries = new List<EventLogEntity>(limit);

        while (visibleEntries.Count < limit)
        {
            var batch = await query
                // SQLite cannot order DateTimeOffset columns directly, so page by the
                // append-only identity and restore timestamp ordering inside the bounded batch.
                .OrderByDescending(entry => entry.Id)
                .Skip(offset)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
            {
                break;
            }

            foreach (var entry in batch
                .OrderByDescending(entry => entry.OccurredAtUtc)
                .ThenByDescending(entry => entry.Id))
            {
                if (!DashboardEventPresentation.ShouldInclude(entry.EventName, entry.Message))
                {
                    continue;
                }

                visibleEntries.Add(entry);
                if (visibleEntries.Count == limit)
                {
                    break;
                }
            }

            if (batch.Count < batchSize)
            {
                break;
            }

            offset += batch.Count;
        }

        return visibleEntries;
    }
}
