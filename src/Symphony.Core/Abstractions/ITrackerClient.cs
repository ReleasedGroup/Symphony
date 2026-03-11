using Symphony.Core.Models;

namespace Symphony.Core.Abstractions;

public interface ITrackerClient
{
    Task<IReadOnlyList<NormalizedIssue>> FetchCandidateIssuesAsync(
        TrackerQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByStatesAsync(
        TrackerQuery query,
        IReadOnlyList<string> states,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IssueStateSnapshot>> FetchIssueStatesByIdsAsync(
        TrackerQuery query,
        IReadOnlyList<string> issueIds,
        CancellationToken cancellationToken = default);

    Task<GitHubGraphQlExecutionResult> ExecuteGitHubGraphQlAsync(
        TrackerQuery query,
        string graphQlDocument,
        string? variablesJson,
        CancellationToken cancellationToken = default);
}
