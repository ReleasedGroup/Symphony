namespace Symphony.Core.Models;

public sealed record TrackerQuery(
    string Endpoint,
    string ApiKey,
    string Owner,
    string Repo,
    IReadOnlyList<string> ActiveStates,
    IReadOnlyList<string> Labels,
    string? Milestone,
    int PageSize = 50);
