using Microsoft.EntityFrameworkCore;
using Symphony.Core.Models;

namespace Symphony.Infrastructure.Persistence.Sqlite;

public static class WorkerLeaseQueries
{
    public static async Task<bool> HasLiveWorkerForRunAsync(
        this SymphonyDbContext dbContext, string runId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var prefix = WorkerLeaseNames.ForRun(runId);
        var leases = await dbContext.InstanceLeases.AsNoTracking()
            .Where(lease => lease.LeaseName.StartsWith(prefix)).ToListAsync(cancellationToken);
        return leases.Any(lease => lease.ExpiresAtUtc > nowUtc);
    }

    public static async Task<bool> HasLiveWorkerForIssueAsync(
        this SymphonyDbContext dbContext, string issueId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var runIds = await dbContext.Runs.Where(run => run.IssueId == issueId).Select(run => run.Id)
            .ToListAsync(cancellationToken);
        foreach (var runId in runIds)
        {
            if (await dbContext.HasLiveWorkerForRunAsync(runId, nowUtc, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }
}
