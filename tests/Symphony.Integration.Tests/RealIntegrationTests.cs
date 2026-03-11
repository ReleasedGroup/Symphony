using System.Net.Http;
using Xunit;
using Symphony.Core.Models;
using Symphony.Infrastructure.Tracker.GitHub;

namespace Symphony.Integration.Tests;

public sealed class RealIntegrationTests
{
    [RealIntegrationFact]
    public async Task GitHubTrackerClient_ShouldQueryConfiguredRepository_WhenEnabled()
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        var owner = Environment.GetEnvironmentVariable("SYMPHONY_REAL_GITHUB_OWNER") ?? "ReleasedGroup";
        var repo = Environment.GetEnvironmentVariable("SYMPHONY_REAL_GITHUB_REPO") ?? "Symphony";

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        var client = new GitHubTrackerClient(httpClient);
        var issues = await client.FetchCandidateIssuesAsync(
            new TrackerQuery(
                Endpoint: "https://api.github.com/graphql",
                ApiKey: token!,
                Owner: owner,
                Repo: repo,
                ActiveStates: ["Open"],
                Labels: [],
                Milestone: null));

        Assert.NotNull(issues);
    }
}

internal sealed class RealIntegrationFactAttribute : FactAttribute
{
    public RealIntegrationFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SYMPHONY_RUN_REAL_INTEGRATION_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip = "Set SYMPHONY_RUN_REAL_INTEGRATION_TESTS=1 to enable real GitHub integration tests.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_TOKEN")))
        {
            Skip = "GITHUB_TOKEN is required when SYMPHONY_RUN_REAL_INTEGRATION_TESTS=1.";
        }
    }
}
