namespace Symphony.Infrastructure.Persistence.Sqlite.Entities;

public sealed class RetryQueueEntity
{
    public string IssueId { get; set; } = string.Empty;
    public string IssueIdentifier { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string OwnerInstanceId { get; set; } = string.Empty;
    public int Attempt { get; set; }
    public DateTimeOffset DueAtUtc { get; set; }
    public string DelayType { get; set; } = string.Empty;
    public string? Error { get; set; }
    public int MaxBackoffMs { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
