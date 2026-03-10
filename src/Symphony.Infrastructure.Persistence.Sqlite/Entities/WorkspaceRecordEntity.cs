namespace Symphony.Infrastructure.Persistence.Sqlite.Entities;

public sealed class WorkspaceRecordEntity
{
    public string IssueId { get; set; } = string.Empty;
    public string IssueIdentifier { get; set; } = string.Empty;
    public string WorkspacePath { get; set; } = string.Empty;
    public string? BranchName { get; set; }
    public DateTimeOffset LastPreparedAtUtc { get; set; }
    public DateTimeOffset? LastCleanedAtUtc { get; set; }
    public string? LastCleanupReason { get; set; }
}
