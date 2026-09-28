namespace Symphony.Infrastructure.Persistence.Sqlite.Entities;

public sealed class ManagedLeaseEntity
{
    public int Id { get; set; } = 1;
    public string? InstanceId { get; set; }
    public string? GenerationId { get; set; }
    public long CurrentEpoch { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UnixEpoch;
}
