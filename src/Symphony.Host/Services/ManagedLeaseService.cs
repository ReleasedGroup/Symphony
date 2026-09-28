using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Symphony.Core.Configuration;
using Symphony.Infrastructure.Persistence.Sqlite;

namespace Symphony.Host.Services;

public sealed record ManagedLeaseRequest(
    string InstanceId,
    string GenerationId,
    long Epoch,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string Signature);

public sealed record ManagedLeaseRenewalResult(
    bool Accepted,
    string? ErrorCode,
    ManagedLeaseDecision Decision);

public sealed class ManagedLeaseService(
    SymphonyDbContext dbContext,
    IOptions<ManagedLeaseOptions> managedOptions,
    ManagedLeaseRuntime runtime,
    TimeProvider timeProvider)
{
    public bool IsEnabled => managedOptions.Value.Enabled;

    public async Task<ManagedLeaseDecision> GetDispatchDecisionAsync(CancellationToken cancellationToken)
    {
        if (!managedOptions.Value.Enabled)
        {
            return runtime.GetLocalDecision();
        }

        var persisted = await dbContext.ManagedLease
            .AsNoTracking()
            .SingleAsync(item => item.Id == 1, cancellationToken);
        return runtime.GetDecision(persisted);
    }

    public async Task<ManagedLeaseRenewalResult> RenewAsync(
        ManagedLeaseRequest request,
        CancellationToken cancellationToken)
    {
        if (!managedOptions.Value.Enabled)
        {
            return Reject("managed_mode_disabled");
        }

        var options = managedOptions.Value;
        if (request.InstanceId != options.InstanceId)
        {
            return Reject("instance_mismatch");
        }

        if (request.GenerationId != options.GenerationId)
        {
            return Reject("generation_mismatch");
        }

        if (request.Epoch <= 0 || request.ExpiresAtUtc <= request.IssuedAtUtc ||
            request.ExpiresAtUtc - request.IssuedAtUtc > TimeSpan.FromSeconds(options.MaxLeaseSeconds))
        {
            return Reject("invalid_lease");
        }

        var nowUtc = timeProvider.GetUtcNow();
        var skew = TimeSpan.FromSeconds(options.MaxClockSkewSeconds);
        if (request.IssuedAtUtc - nowUtc > skew)
        {
            return Reject("clock_skew_exceeded");
        }

        if (nowUtc >= request.ExpiresAtUtc - skew)
        {
            return Reject("lease_expired");
        }

        if (!HasValidSignature(request, options.SigningKeyReference))
        {
            return Reject("invalid_signature");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE managed_lease SET Id = Id WHERE Id = 1", cancellationToken);
        var persisted = await dbContext.ManagedLease.SingleAsync(item => item.Id == 1, cancellationToken);
        await dbContext.Entry(persisted).ReloadAsync(cancellationToken);
        if (request.Epoch < persisted.CurrentEpoch ||
            (request.Epoch == persisted.CurrentEpoch && request.ExpiresAtUtc < persisted.ExpiresAtUtc))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Reject("stale_epoch");
        }

        persisted.InstanceId = request.InstanceId;
        persisted.GenerationId = request.GenerationId;
        persisted.CurrentEpoch = request.Epoch;
        persisted.ExpiresAtUtc = request.ExpiresAtUtc;
        persisted.UpdatedAtUtc = nowUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        runtime.Apply(request.Epoch, request.ExpiresAtUtc);
        return new ManagedLeaseRenewalResult(true, null, runtime.GetLocalDecision());
    }

    private ManagedLeaseRenewalResult Reject(string code)
        => new(false, code, runtime.GetLocalDecision());

    private static bool HasValidSignature(ManagedLeaseRequest request, string keyReference)
    {
        if (string.IsNullOrWhiteSpace(request.Signature) ||
            string.IsNullOrWhiteSpace(keyReference) || keyReference.Length < 2 ||
            !keyReference.StartsWith('$'))
        {
            return false;
        }

        var keyValue = Environment.GetEnvironmentVariable(keyReference[1..]);
        if (string.IsNullOrEmpty(keyValue))
        {
            return false;
        }

        byte[] supplied;
        try
        {
            supplied = Convert.FromBase64String(request.Signature);
        }
        catch (FormatException)
        {
            return false;
        }

        var payload = $"{request.InstanceId}\n{request.GenerationId}\n{request.Epoch}\n" +
            $"{request.IssuedAtUtc.ToUnixTimeMilliseconds()}\n{request.ExpiresAtUtc.ToUnixTimeMilliseconds()}";
        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(keyValue), Encoding.UTF8.GetBytes(payload));
        return supplied.Length == expected.Length &&
            CryptographicOperations.FixedTimeEquals(supplied, expected);
    }
}
