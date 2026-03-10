namespace Symphony.Infrastructure.Persistence.Sqlite.Entities;

public sealed class IssueCacheEntity
{
    public string IssueId { get; set; } = string.Empty;
    public string Identifier { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? Priority { get; set; }
    public string State { get; set; } = string.Empty;
    public string? BranchName { get; set; }
    public string? Url { get; set; }
    public string? Milestone { get; set; }
    public string LabelsJson { get; set; } = "[]";
    public string PullRequestsJson { get; set; } = "[]";
    public string BlockedByJson { get; set; } = "[]";
    public DateTimeOffset? CreatedAtUtc { get; set; }
    public DateTimeOffset? UpdatedAtUtc { get; set; }
    public DateTimeOffset CachedAtUtc { get; set; }
}
