namespace Symphony.Core.Models;

public sealed record NormalizedIssue(
    string Id,
    string Identifier,
    string Title,
    string? Description,
    int? Priority,
    string State,
    string? BranchName,
    string? Url,
    string? Milestone,
    IReadOnlyList<string> Labels,
    IReadOnlyList<PullRequestRef> PullRequests,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt);
