using Microsoft.EntityFrameworkCore;
using Symphony.Core.Abstractions;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;

namespace Symphony.Infrastructure.Persistence.Sqlite;

public sealed class OrchestrationCoordinationStore(
    SymphonyDbContext dbContext,
    TimeProvider timeProvider) : IOrchestrationCoordinationStore
{
    public async Task<bool> AcquireOrRenewLeaseAsync(
        string leaseName,
        string instanceId,
        TimeSpan leaseTtl,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var existingLease = await dbContext.InstanceLeases
            .SingleOrDefaultAsync(item => item.LeaseName == leaseName, cancellationToken);

        if (existingLease is null)
        {
            dbContext.InstanceLeases.Add(new InstanceLeaseEntity
            {
                LeaseName = leaseName,
                OwnerInstanceId = instanceId,
                AcquiredAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc.Add(leaseTtl),
                UpdatedAtUtc = nowUtc
            });

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        if (!existingLease.OwnerInstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase) &&
            existingLease.ExpiresAtUtc > nowUtc)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        if (existingLease.OwnerInstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
        {
            existingLease.UpdatedAtUtc = nowUtc;
            existingLease.ExpiresAtUtc = nowUtc.Add(leaseTtl);
        }
        else
        {
            existingLease.OwnerInstanceId = instanceId;
            existingLease.AcquiredAtUtc = nowUtc;
            existingLease.UpdatedAtUtc = nowUtc;
            existingLease.ExpiresAtUtc = nowUtc.Add(leaseTtl);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task ReleaseLeaseAsync(string leaseName, string instanceId, CancellationToken cancellationToken = default)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var existingLease = await dbContext.InstanceLeases
            .SingleOrDefaultAsync(item => item.LeaseName == leaseName, cancellationToken);

        if (existingLease is null || !existingLease.OwnerInstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        existingLease.ExpiresAtUtc = nowUtc;
        existingLease.UpdatedAtUtc = nowUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> TryClaimIssueAsync(
        string issueId,
        string issueIdentifier,
        string leaseName,
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var activeClaim = await dbContext.DispatchClaims
            .SingleOrDefaultAsync(item => item.IssueId == issueId && item.Status == "active", cancellationToken);

        if (activeClaim is not null)
        {
            if (activeClaim.ClaimedByInstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))
            {
                activeClaim.UpdatedAtUtc = nowUtc;
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return true;
            }

            var matchingLeases = await dbContext.InstanceLeases
                .Where(item =>
                    item.LeaseName == leaseName &&
                    item.OwnerInstanceId == activeClaim.ClaimedByInstanceId)
                .ToListAsync(cancellationToken);
            var ownerHasLiveLease = matchingLeases.Any(item => item.ExpiresAtUtc > nowUtc);

            if (ownerHasLiveLease)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            activeClaim.ClaimedByInstanceId = instanceId;
            activeClaim.ClaimedAtUtc = nowUtc;
            activeClaim.UpdatedAtUtc = nowUtc;
            activeClaim.ReleasedAtUtc = null;
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        dbContext.DispatchClaims.Add(new DispatchClaimEntity
        {
            IssueId = issueId,
            IssueIdentifier = issueIdentifier,
            ClaimedByInstanceId = instanceId,
            ClaimedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            Status = "active"
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }
    }

    public async Task ReleaseIssueClaimAsync(
        string issueId,
        string instanceId,
        string releaseStatus,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var activeClaims = await dbContext.DispatchClaims
            .Where(item =>
                item.IssueId == issueId &&
                item.Status == "active" &&
                item.ClaimedByInstanceId == instanceId)
            .ToListAsync(cancellationToken);

        if (activeClaims.Count == 0)
        {
            return;
        }

        foreach (var claim in activeClaims)
        {
            claim.Status = releaseStatus;
            claim.ReleasedAtUtc = nowUtc;
            claim.UpdatedAtUtc = nowUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
