using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite;

namespace Symphony.Host.Services;

public sealed record ManagementStatus(
    string State,
    bool Paused,
    int ActiveRuns,
    int PendingRetries,
    bool Quiescent,
    DateTimeOffset UpdatedAtUtc);

public sealed class ManagedControlService(
    SymphonyDbContext dbContext,
    TimeProvider timeProvider)
{
    public async Task<bool> IsPausedAsync(CancellationToken cancellationToken)
        => await dbContext.ManagementControl
            .AsNoTracking()
            .Where(control => control.Id == 1)
            .Select(control => control.Paused)
            .SingleAsync(cancellationToken);

    public async Task<ManagementStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var control = await dbContext.ManagementControl
            .AsNoTracking()
            .SingleAsync(item => item.Id == 1, cancellationToken);
        var activeRuns = await dbContext.Runs
            .AsNoTracking()
            .CountAsync(run => run.Status == RunStatusNames.Running, cancellationToken);
        var pendingRetries = await dbContext.RetryQueue
            .AsNoTracking()
            .CountAsync(cancellationToken);
        var quiescent = control.Paused && activeRuns == 0;
        return new ManagementStatus(
            control.Paused ? (quiescent ? "suspended" : "draining") : "running",
            control.Paused,
            activeRuns,
            pendingRetries,
            quiescent,
            control.UpdatedAtUtc);
    }

    public async Task<ManagementStatus> SetPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        await dbContext.ManagementControl
            .Where(control => control.Id == 1 && control.Paused != paused)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(control => control.Paused, paused)
                .SetProperty(control => control.UpdatedAtUtc, timeProvider.GetUtcNow()), cancellationToken);
        return await GetStatusAsync(cancellationToken);
    }

    public async Task<ManagementStatus> DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var status = await SetPausedAsync(true, cancellationToken);
        var deadline = timeProvider.GetUtcNow().Add(timeout);
        while (!status.Quiescent && timeProvider.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            status = await GetStatusAsync(cancellationToken);
        }

        return status;
    }

    // SQLite's write transaction serializes a dispatch start with a pause update.
    // Once pause returns, no later dispatch can have passed this gate.
    public async Task<IDbContextTransaction?> TryEnterDispatchAsync(CancellationToken cancellationToken)
    {
        var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await dbContext.Database.ExecuteSqlRawAsync(
                "UPDATE management_control SET Id = Id WHERE Id = 1", cancellationToken);
            if (await IsPausedAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
                return null;
            }

            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}
