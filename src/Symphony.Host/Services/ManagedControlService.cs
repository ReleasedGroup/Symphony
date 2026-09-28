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
    DateTimeOffset UpdatedAtUtc,
    long? CurrentEpoch,
    DateTimeOffset? LeaseExpiresAtUtc,
    string? DispatchDeniedReason);

public sealed class ManagedLeaseDeniedException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}

public sealed class ManagedControlService(
    SymphonyDbContext dbContext,
    TimeProvider timeProvider,
    ManagedLeaseService managedLeaseService)
{
    public Task<ManagedLeaseDecision> GetDispatchDecisionAsync(CancellationToken cancellationToken)
        => managedLeaseService.GetDispatchDecisionAsync(cancellationToken);

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
        var leaseDecision = await managedLeaseService.GetDispatchDecisionAsync(cancellationToken);
        var persistedLease = managedLeaseService.IsEnabled
            ? await dbContext.ManagedLease.AsNoTracking()
                .SingleAsync(item => item.Id == 1, cancellationToken)
            : null;
        var quiescent = control.Paused && activeRuns == 0;
        return new ManagementStatus(
            control.Paused ? (quiescent ? "suspended" : "draining") : "running",
            control.Paused,
            activeRuns,
            pendingRetries,
            quiescent,
            control.UpdatedAtUtc,
            persistedLease is null
                ? leaseDecision.Epoch
                : persistedLease.CurrentEpoch > 0 ? persistedLease.CurrentEpoch : null,
            persistedLease is null ? leaseDecision.ExpiresAtUtc : persistedLease.ExpiresAtUtc,
            leaseDecision.DeniedReason);
    }

    public async Task<ManagementStatus> SetPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        if (!paused)
        {
            var leaseDecision = await managedLeaseService.GetDispatchDecisionAsync(cancellationToken);
            if (!leaseDecision.Allowed)
            {
                throw new ManagedLeaseDeniedException(leaseDecision.DeniedReason ?? "lease_required");
            }
        }

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
        while (!status.Quiescent)
        {
            var remaining = deadline - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining < TimeSpan.FromMilliseconds(100)
                ? remaining
                : TimeSpan.FromMilliseconds(100), cancellationToken);
            status = await GetStatusAsync(cancellationToken);
        }

        return status;
    }

    // SQLite's write transaction serializes a dispatch start with pause and
    // persisted epoch updates. The caller holds this gate through TryStartAsync.
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

            var leaseDecision = await managedLeaseService.GetDispatchDecisionAsync(cancellationToken);
            if (!leaseDecision.Allowed)
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
