namespace Symphony.Infrastructure.Persistence.Sqlite.Entities;

public sealed class ManagementControlEntity
{
    public int Id { get; set; } = 1;
    public bool Paused { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UnixEpoch;
}
