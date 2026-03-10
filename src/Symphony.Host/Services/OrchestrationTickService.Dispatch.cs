using Microsoft.EntityFrameworkCore;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

public sealed partial class OrchestrationTickService
{
    private async Task DispatchCandidatesAsync(
        WorkflowDefinition workflowDefinition,
        string apiKey,
        string instanceId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<NormalizedIssue> issues;
        var query = BuildTrackerQuery(workflowDefinition, apiKey);
        try
        {
            issues = await trackerClient.FetchCandidateIssuesAsync(query, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Candidate fetch failed for {Owner}/{Repo}. Dispatch will be skipped this tick.",
                query.Owner,
                query.Repo);
            return;
        }

        await UpsertIssueCacheAsync(issues, cancellationToken);

        var runningIssues = await dbContext.Runs
            .Where(run => run.Status == RunStatusNames.Running)
            .ToListAsync(cancellationToken);

        var countsByState = runningIssues
            .GroupBy(run => NormalizeStateKey(run.State), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var runningIssueIds = new HashSet<string>(runningIssues.Select(run => run.IssueId), StringComparer.OrdinalIgnoreCase);

        var dueRetries = await dbContext.RetryQueue
            .FromSqlInterpolated($"""
                SELECT *
                FROM retry_queue
                WHERE DueAtUtc <= {timeProvider.GetUtcNow()}
                ORDER BY DueAtUtc
                """)
            .ToListAsync(cancellationToken);

        var candidatesById = issues.ToDictionary(issue => issue.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var retryEntry in dueRetries)
        {
            if (!candidatesById.TryGetValue(retryEntry.IssueId, out var retryIssue))
            {
                await ReleaseRetryReservationAsync(
                    retryEntry.IssueId,
                    retryEntry.IssueIdentifier,
                    instanceId,
                    "retry candidate missing",
                    cancellationToken);
                continue;
            }

            if (!IsDispatchEligible(retryIssue, workflowDefinition, runningIssueIds, countsByState))
            {
                await ReleaseRetryReservationAsync(
                    retryEntry.IssueId,
                    retryEntry.IssueIdentifier,
                    instanceId,
                    "issue no longer eligible for dispatch",
                    cancellationToken);
                continue;
            }

            if (!HasGlobalSlot(workflowDefinition, runningIssueIds.Count) ||
                !HasStateSlot(retryIssue.State, workflowDefinition, countsByState))
            {
                await RescheduleRetryAsync(
                    retryEntry,
                    instanceId,
                    "no available orchestrator slots",
                    workflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                    cancellationToken);
                continue;
            }

            if (await DispatchIssueAsync(retryIssue, workflowDefinition, instanceId, retryEntry.Attempt, countsByState, cancellationToken))
            {
                runningIssueIds.Add(retryIssue.Id);
            }
        }

        foreach (var issue in OrderIssuesForDispatch(issues))
        {
            if (!HasGlobalSlot(workflowDefinition, runningIssueIds.Count))
            {
                break;
            }

            if (!IsDispatchEligible(issue, workflowDefinition, runningIssueIds, countsByState))
            {
                continue;
            }

            if (await DispatchIssueAsync(issue, workflowDefinition, instanceId, attempt: null, countsByState, cancellationToken))
            {
                runningIssueIds.Add(issue.Id);
            }
        }
    }

    private async Task<bool> DispatchIssueAsync(
        NormalizedIssue issue,
        WorkflowDefinition workflowDefinition,
        string instanceId,
        int? attempt,
        Dictionary<string, int> countsByState,
        CancellationToken cancellationToken)
    {
        var claimed = await coordinationStore.TryClaimIssueAsync(
            issue.Id,
            issue.Identifier,
            ResolveLeaseName(),
            instanceId,
            cancellationToken);

        if (!claimed)
        {
            return false;
        }

        var nowUtc = timeProvider.GetUtcNow();
        var run = await dbContext.Runs
            .Where(runEntity =>
                runEntity.IssueId == issue.Id &&
                (runEntity.Status == RunStatusNames.Running || runEntity.Status == RunStatusNames.Retrying))
            .SingleOrDefaultAsync(cancellationToken);

        if (run is null)
        {
            run = new RunEntity
            {
                Id = Guid.NewGuid().ToString("N"),
                IssueId = issue.Id,
                IssueIdentifier = issue.Identifier,
                OwnerInstanceId = instanceId,
                Status = RunStatusNames.Running,
                State = issue.State,
                CurrentRetryAttempt = attempt,
                StartedAtUtc = nowUtc
            };
            dbContext.Runs.Add(run);
        }
        else
        {
            run.OwnerInstanceId = instanceId;
            run.Status = RunStatusNames.Running;
            run.State = issue.State;
            run.CurrentRetryAttempt = attempt;
            run.CompletedAtUtc = null;
            run.RequestedStopReason = null;
            run.CleanupWorkspaceOnStop = false;
        }

        var runAttempt = new RunAttemptEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            RunId = run.Id,
            IssueId = issue.Id,
            AttemptNumber = attempt,
            Status = RunStatusNames.Running,
            StartedAtUtc = nowUtc
        };
        dbContext.RunAttempts.Add(runAttempt);

        var retryEntry = await dbContext.RetryQueue.SingleOrDefaultAsync(
            retry => retry.IssueId == issue.Id,
            cancellationToken);
        if (retryEntry is not null)
        {
            dbContext.RetryQueue.Remove(retryEntry);
        }

        dbContext.EventLog.Add(new EventLogEntity
        {
            IssueId = issue.Id,
            IssueIdentifier = issue.Identifier,
            RunId = run.Id,
            RunAttemptId = runAttempt.Id,
            EventName = "issue_dispatched",
            Level = LogLevel.Information.ToString(),
            Message = $"Issue {issue.Identifier} dispatched with attempt {(attempt.HasValue ? attempt.Value.ToString() : "initial")}.",
            OccurredAtUtc = nowUtc
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        var started = await issueExecutionCoordinator.TryStartAsync(
            new IssueExecutionRequest(
                run.Id,
                runAttempt.Id,
                instanceId,
                attempt,
                issue,
                workflowDefinition),
            cancellationToken);

        if (!started)
        {
            run.Status = RunStatusNames.Retrying;
            run.CurrentRetryAttempt = attempt.HasValue ? attempt.Value + 1 : 1;
            run.LastEvent = "dispatch_failed";
            run.LastMessage = "Issue execution coordinator rejected the dispatch request.";
            run.LastEventAtUtc = nowUtc;
            runAttempt.Status = RunStatusNames.Failed;
            runAttempt.Error = "Issue execution coordinator rejected the dispatch request.";
            runAttempt.CompletedAtUtc = nowUtc;

            dbContext.RetryQueue.Add(new RetryQueueEntity
            {
                IssueId = issue.Id,
                IssueIdentifier = issue.Identifier,
                RunId = run.Id,
                OwnerInstanceId = instanceId,
                Attempt = run.CurrentRetryAttempt.Value,
                DueAtUtc = nowUtc.AddSeconds(10),
                DelayType = RetryDelayTypes.Backoff,
                Error = "failed to start issue execution coordinator",
                MaxBackoffMs = workflowDefinition.Runtime.Agent.MaxRetryBackoffMs,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            });

            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        var stateKey = NormalizeStateKey(issue.State);
        countsByState[stateKey] = countsByState.GetValueOrDefault(stateKey) + 1;
        return true;
    }

    private bool IsDispatchEligible(
        NormalizedIssue issue,
        WorkflowDefinition workflowDefinition,
        HashSet<string> runningIssueIds,
        IReadOnlyDictionary<string, int> countsByState)
    {
        if (string.IsNullOrWhiteSpace(issue.Id) ||
            string.IsNullOrWhiteSpace(issue.Identifier) ||
            string.IsNullOrWhiteSpace(issue.Title) ||
            string.IsNullOrWhiteSpace(issue.State))
        {
            return false;
        }

        if (runningIssueIds.Contains(issue.Id))
        {
            return false;
        }

        if (MatchesTerminalState(issue.State, workflowDefinition.Runtime.Tracker.TerminalStates))
        {
            return false;
        }

        if (!IssueStateMatcher.MatchesConfiguredActiveState(issue.State, workflowDefinition.Runtime.Tracker.ActiveStates))
        {
            return false;
        }

        if (!HasStateSlot(issue.State, workflowDefinition, countsByState))
        {
            return false;
        }

        return PassesBlockerRule(issue, workflowDefinition);
    }

    private static bool PassesBlockerRule(NormalizedIssue issue, WorkflowDefinition workflowDefinition)
    {
        if (!NormalizeStateKey(issue.State).Equals("todo", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return issue.BlockedBy.All(blocker =>
            string.IsNullOrWhiteSpace(blocker.State) ||
            MatchesTerminalState(blocker.State, workflowDefinition.Runtime.Tracker.TerminalStates));
    }

    private static bool HasGlobalSlot(WorkflowDefinition workflowDefinition, int runningCount)
    {
        return runningCount < workflowDefinition.Runtime.Agent.MaxConcurrentAgents;
    }

    private static bool HasStateSlot(
        string state,
        WorkflowDefinition workflowDefinition,
        IReadOnlyDictionary<string, int> countsByState)
    {
        var stateKey = NormalizeStateKey(state);
        var currentCount = countsByState.GetValueOrDefault(stateKey);
        if (!workflowDefinition.Runtime.Agent.MaxConcurrentAgentsByState.TryGetValue(stateKey, out var limit))
        {
            limit = workflowDefinition.Runtime.Agent.MaxConcurrentAgents;
        }

        return currentCount < limit;
    }

    private async Task ReleaseRetryReservationAsync(
        string issueId,
        string issueIdentifier,
        string instanceId,
        string reason,
        CancellationToken cancellationToken)
    {
        var run = await dbContext.Runs
            .Where(runEntity => runEntity.IssueId == issueId && runEntity.Status == RunStatusNames.Retrying)
            .ToListAsync(cancellationToken);

        var latestRun = run
            .OrderByDescending(runEntity => runEntity.StartedAtUtc)
            .FirstOrDefault();

        if (latestRun is not null)
        {
            latestRun.Status = RunStatusNames.ReleasedIneligible;
            latestRun.CompletedAtUtc = timeProvider.GetUtcNow();
            latestRun.LastEvent = "claim_released";
            latestRun.LastMessage = reason;
            latestRun.LastEventAtUtc = timeProvider.GetUtcNow();
        }

        var retryEntry = await dbContext.RetryQueue.SingleOrDefaultAsync(
            retry => retry.IssueId == issueId,
            cancellationToken);
        if (retryEntry is not null)
        {
            dbContext.RetryQueue.Remove(retryEntry);
        }

        await coordinationStore.ReleaseIssueClaimAsync(issueId, instanceId, RunStatusNames.ReleasedIneligible, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task RescheduleRetryAsync(
        RetryQueueEntity retryEntry,
        string instanceId,
        string error,
        int maxRetryBackoffMs,
        CancellationToken cancellationToken)
    {
        var nextAttempt = retryEntry.Attempt + 1;
        retryEntry.OwnerInstanceId = instanceId;
        retryEntry.Attempt = nextAttempt;
        retryEntry.Error = error;
        retryEntry.DelayType = RetryDelayTypes.Backoff;
        retryEntry.MaxBackoffMs = maxRetryBackoffMs;
        retryEntry.DueAtUtc = timeProvider.GetUtcNow().AddMilliseconds(ComputeBackoffMs(nextAttempt, maxRetryBackoffMs));
        retryEntry.UpdatedAtUtc = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static int ComputeBackoffMs(int attempt, int maxRetryBackoffMs)
    {
        var exponent = Math.Max(attempt - 1, 0);
        var delayMs = 10_000 * (int)Math.Pow(2, exponent);
        return Math.Min(delayMs, maxRetryBackoffMs);
    }

    private static string NormalizeStateKey(string state) => state.Trim().ToLowerInvariant();

    private static bool MatchesTerminalState(string state, IReadOnlyList<string> terminalStates)
    {
        if (terminalStates.Count == 0)
        {
            return IssueStateMatcher.IsClosedState(state);
        }

        return terminalStates.Any(terminalState =>
            terminalState.Trim().Equals(state.Trim(), StringComparison.OrdinalIgnoreCase) ||
            (IssueStateMatcher.IsClosedState(terminalState) && IssueStateMatcher.IsClosedState(state)));
    }

    private static IEnumerable<NormalizedIssue> OrderIssuesForDispatch(IEnumerable<NormalizedIssue> issues)
    {
        return issues
            .OrderBy(issue => issue.Priority.HasValue ? 0 : 1)
            .ThenBy(issue => issue.Priority ?? int.MaxValue)
            .ThenBy(issue => issue.CreatedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(issue => issue.Identifier, StringComparer.OrdinalIgnoreCase)
            .ThenBy(issue => issue.Id, StringComparer.OrdinalIgnoreCase);
    }
}
