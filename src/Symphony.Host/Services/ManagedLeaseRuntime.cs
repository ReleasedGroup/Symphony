using Microsoft.Extensions.Options;
using Symphony.Core.Configuration;
using Symphony.Infrastructure.Persistence.Sqlite.Entities;

namespace Symphony.Host.Services;

public sealed record ManagedLeaseDecision(
    bool Allowed,
    long? Epoch,
    DateTimeOffset? ExpiresAtUtc,
    string? DeniedReason);

public sealed class ManagedLeaseRuntime(
    IOptions<ManagedLeaseOptions> managedOptions,
    TimeProvider timeProvider) : IDisposable
{
    private readonly object sync = new();
    private CancellationTokenSource? leaseCancellation;
    private long? epoch;
    private DateTimeOffset? expiresAtUtc;
    private string? deniedReason;

    public ManagedLeaseDecision GetLocalDecision()
    {
        if (!managedOptions.Value.Enabled)
        {
            return new ManagedLeaseDecision(true, null, null, null);
        }

        lock (sync)
        {
            if (leaseCancellation is null)
            {
                return new ManagedLeaseDecision(false, epoch, expiresAtUtc,
                    deniedReason ?? "lease_not_renewed");
            }

            var safeExpiry = expiresAtUtc!.Value.AddSeconds(-managedOptions.Value.MaxClockSkewSeconds);
            if (leaseCancellation.IsCancellationRequested || timeProvider.GetUtcNow() >= safeExpiry)
            {
                leaseCancellation.Cancel();
                return new ManagedLeaseDecision(false, epoch, expiresAtUtc,
                    deniedReason ?? "lease_expired");
            }

            return new ManagedLeaseDecision(true, epoch, expiresAtUtc, null);
        }
    }

    public ManagedLeaseDecision GetDecision(ManagedLeaseEntity persisted)
    {
        var local = GetLocalDecision();
        if (!local.Allowed)
        {
            return local;
        }

        if (!managedOptions.Value.Enabled)
        {
            return local;
        }

        if (persisted.CurrentEpoch != local.Epoch ||
            persisted.GenerationId != managedOptions.Value.GenerationId ||
            persisted.InstanceId != managedOptions.Value.InstanceId ||
            persisted.ExpiresAtUtc != local.ExpiresAtUtc)
        {
            return new ManagedLeaseDecision(false, local.Epoch, local.ExpiresAtUtc,
                "lease_epoch_mismatch");
        }

        return local;
    }

    public CancellationToken GetRunCancellationToken()
    {
        if (!managedOptions.Value.Enabled)
        {
            return CancellationToken.None;
        }

        lock (sync)
        {
            return leaseCancellation?.Token ?? new CancellationToken(canceled: true);
        }
    }

    public void Apply(long newEpoch, DateTimeOffset newExpiryUtc)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var remaining = newExpiryUtc - nowUtc -
            TimeSpan.FromSeconds(managedOptions.Value.MaxClockSkewSeconds);
        if (remaining <= TimeSpan.Zero)
        {
            Invalidate("lease_expired");
            return;
        }

        lock (sync)
        {
            if (epoch.HasValue && newEpoch < epoch.Value)
            {
                return;
            }

            if (epoch == newEpoch && leaseCancellation is { IsCancellationRequested: false })
            {
                if (expiresAtUtc >= newExpiryUtc)
                {
                    return;
                }

                leaseCancellation.CancelAfter(remaining);
                expiresAtUtc = newExpiryUtc;
                return;
            }

            leaseCancellation?.Cancel();
            leaseCancellation?.Dispose();
            leaseCancellation = new CancellationTokenSource();
            leaseCancellation.CancelAfter(remaining);
            epoch = newEpoch;
            expiresAtUtc = newExpiryUtc;
            deniedReason = null;
        }
    }

    public void Invalidate(string reason)
    {
        lock (sync)
        {
            deniedReason = reason;
            leaseCancellation?.Cancel();
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            leaseCancellation?.Cancel();
            leaseCancellation?.Dispose();
            leaseCancellation = null;
        }
    }
}
