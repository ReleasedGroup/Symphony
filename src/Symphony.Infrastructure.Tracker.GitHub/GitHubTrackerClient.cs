namespace Symphony.Infrastructure.Tracker.GitHub;

public sealed class GitHubTrackerClient
{
    public Task<IReadOnlyList<string>> FetchCandidateIssueIdsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<string>>([]);
    }
}
