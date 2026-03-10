namespace Symphony.Infrastructure.Persistence.Sqlite.Entities;

public sealed class RunAttemptEntity
{
    public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public RunEntity? Run { get; set; }
    public string IssueId { get; set; } = string.Empty;
    public int? AttemptNumber { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Error { get; set; }
    public string? WorkspacePath { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}
