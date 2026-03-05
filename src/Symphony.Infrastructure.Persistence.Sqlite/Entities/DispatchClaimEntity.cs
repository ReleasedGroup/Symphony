namespace Symphony.Infrastructure.Persistence.Sqlite.Entities;

public sealed class DispatchClaimEntity
{
    public long Id { get; set; }
    public string IssueId { get; set; } = string.Empty;
    public string IssueIdentifier { get; set; } = string.Empty;
    public string ClaimedByInstanceId { get; set; } = string.Empty;
    public DateTimeOffset ClaimedAtUtc { get; set; }
    public DateTimeOffset? ReleasedAtUtc { get; set; }
    public string Status { get; set; } = "active";
}
