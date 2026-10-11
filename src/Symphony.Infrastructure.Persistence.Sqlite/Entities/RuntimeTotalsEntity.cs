namespace Symphony.Infrastructure.Persistence.Sqlite.Entities;

// Constant-size, issue-free accounting for runs removed by terminal cleanup.
public sealed class RuntimeTotalsEntity
{
    public int Id { get; set; } = 1;
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public double SecondsRunning { get; set; }
    public long RateLimitsEventId { get; set; }
    public string? RateLimitsJson { get; set; }
}
