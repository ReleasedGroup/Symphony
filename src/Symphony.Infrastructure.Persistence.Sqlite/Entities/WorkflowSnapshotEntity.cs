namespace Symphony.Infrastructure.Persistence.Sqlite.Entities;

public sealed class WorkflowSnapshotEntity
{
    public long Id { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string ConfigHash { get; set; } = string.Empty;
    public string RuntimeJson { get; set; } = string.Empty;
    public DateTimeOffset LoadedAtUtc { get; set; }
}
