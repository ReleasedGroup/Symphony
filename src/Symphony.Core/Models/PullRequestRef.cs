namespace Symphony.Core.Models;

public sealed record PullRequestRef(
    string? Id,
    int? Number,
    string? State,
    string? Url,
    string? HeadRef,
    string? BaseRef);
