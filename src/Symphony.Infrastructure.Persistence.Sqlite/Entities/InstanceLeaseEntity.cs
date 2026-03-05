namespace Symphony.Infrastructure.Persistence.Sqlite.Entities;

public sealed class InstanceLeaseEntity
{
    public long Id { get; set; }
    public string LeaseName { get; set; } = string.Empty;
    public string OwnerInstanceId { get; set; } = string.Empty;
    public DateTimeOffset AcquiredAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
