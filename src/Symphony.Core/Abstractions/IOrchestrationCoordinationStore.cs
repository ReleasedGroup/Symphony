namespace Symphony.Core.Abstractions;

public interface IOrchestrationCoordinationStore
{
    Task<bool> AcquireOrRenewLeaseAsync(
        string leaseName,
        string instanceId,
        TimeSpan leaseTtl,
        CancellationToken cancellationToken = default);

    Task ReleaseLeaseAsync(
        string leaseName,
        string instanceId,
        CancellationToken cancellationToken = default);

    Task<bool> TryClaimIssueAsync(
        string issueId,
        string issueIdentifier,
        string instanceId,
        CancellationToken cancellationToken = default);

    Task ReleaseIssueClaimAsync(
        string issueId,
        string instanceId,
        string releaseStatus,
        CancellationToken cancellationToken = default);
}
