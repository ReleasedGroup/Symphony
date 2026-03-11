using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite;

namespace Symphony.Host.Services;

public sealed class RuntimeStateService(
    SymphonyDbContext dbContext,
    TimeProvider timeProvider)
{
    public async Task<object> GetStateAsync(CancellationToken cancellationToken)
    {
        var generatedAt = timeProvider.GetUtcNow();
        var runningRuns = (await dbContext.Runs
            .Where(run => run.Status == RunStatusNames.Running)
            .ToListAsync(cancellationToken))
            .OrderBy(run => run.StartedAtUtc)
            .ToList();
        var retryEntries = (await dbContext.RetryQueue
            .ToListAsync(cancellationToken))
            .OrderBy(entry => entry.DueAtUtc)
            .ToList();
        var attempts = await dbContext.RunAttempts.ToListAsync(cancellationToken);
        var latestRateLimitsJson = (await dbContext.EventLog
            .Where(entry => entry.EventName == "rate_limits_updated" && entry.DataJson != null)
            .ToListAsync(cancellationToken))
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .Select(entry => entry.DataJson)
            .FirstOrDefault();

        var secondsRunning = attempts
            .Where(attempt => attempt.CompletedAtUtc.HasValue)
            .Sum(attempt => Math.Max((attempt.CompletedAtUtc!.Value - attempt.StartedAtUtc).TotalSeconds, 0d));
        secondsRunning += attempts
            .Where(attempt => attempt.Status == RunStatusNames.Running && attempt.CompletedAtUtc is null)
            .Sum(attempt => Math.Max((generatedAt - attempt.StartedAtUtc).TotalSeconds, 0d));

        return new
        {
            generated_at = generatedAt,
            counts = new
            {
                running = runningRuns.Count,
                retrying = retryEntries.Count
            },
            running = runningRuns.Select(run => new
            {
                issue_id = run.IssueId,
                issue_identifier = run.IssueIdentifier,
                state = run.State,
                session_id = run.SessionId,
                turn_count = run.TurnCount,
                last_event = run.LastEvent,
                last_message = run.LastMessage,
                started_at = run.StartedAtUtc,
                last_event_at = run.LastEventAtUtc,
                tokens = new
                {
                    input_tokens = run.InputTokens,
                    output_tokens = run.OutputTokens,
                    total_tokens = run.TotalTokens
                }
            }),
            retrying = retryEntries.Select(entry => new
            {
                issue_id = entry.IssueId,
                issue_identifier = entry.IssueIdentifier,
                attempt = entry.Attempt,
                due_at = entry.DueAtUtc,
                error = entry.Error
            }),
            codex_totals = new
            {
                input_tokens = runningRuns.Sum(run => run.InputTokens) + await dbContext.Runs
                    .Where(run => run.Status != RunStatusNames.Running)
                    .SumAsync(run => run.InputTokens, cancellationToken),
                output_tokens = runningRuns.Sum(run => run.OutputTokens) + await dbContext.Runs
                    .Where(run => run.Status != RunStatusNames.Running)
                    .SumAsync(run => run.OutputTokens, cancellationToken),
                total_tokens = runningRuns.Sum(run => run.TotalTokens) + await dbContext.Runs
                    .Where(run => run.Status != RunStatusNames.Running)
                    .SumAsync(run => run.TotalTokens, cancellationToken),
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
            .Where(run => run.IssueIdentifier == issueIdentifier)
            .ToListAsync(cancellationToken))
            .OrderByDescending(run => run.StartedAtUtc)
            .FirstOrDefault();
        var workspaceRecord = await dbContext.WorkspaceRecords
            .SingleOrDefaultAsync(record => record.IssueIdentifier == issueIdentifier, cancellationToken);
        var retryEntry = await dbContext.RetryQueue
            .SingleOrDefaultAsync(entry => entry.IssueIdentifier == issueIdentifier, cancellationToken);
        var issueCache = await dbContext.IssueCache
            .SingleOrDefaultAsync(entry => entry.Identifier == issueIdentifier, cancellationToken);
        var recentEvents = (await dbContext.EventLog
            .Where(entry => entry.IssueIdentifier == issueIdentifier)
            .ToListAsync(cancellationToken))
            .OrderByDescending(entry => entry.OccurredAtUtc)
            .Take(20)
            .ToList();

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
                    last_message = latestRun.LastMessage,
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
                    message = entry.Message
                }),
            last_error = lastError,
            tracked = new
            {
                cache_state = issueCache?.State,
                milestone = issueCache?.Milestone,
                labels = ParseJsonValue(issueCache?.LabelsJson)
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
}
