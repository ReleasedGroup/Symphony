namespace Symphony.Infrastructure.Persistence.Sqlite.Entities;

public sealed class EventLogEntity
{
    public long Id { get; set; }
    public string? IssueId { get; set; }
    public string? IssueIdentifier { get; set; }
    public string? RunId { get; set; }
    public string? RunAttemptId { get; set; }
    public string? SessionId { get; set; }
    public string EventName { get; set; } = string.Empty;
    public string Level { get; set; } = "Information";
    public string Message { get; set; } = string.Empty;
    public string? DataJson { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}
