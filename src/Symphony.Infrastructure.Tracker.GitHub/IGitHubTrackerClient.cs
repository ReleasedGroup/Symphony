using Symphony.Core.Models;

namespace Symphony.Infrastructure.Tracker.GitHub;

public interface IGitHubTrackerClient
{
    Task<IReadOnlyList<NormalizedIssue>> FetchCandidateIssuesAsync(
        TrackerQuery query,
        CancellationToken cancellationToken = default);
}
