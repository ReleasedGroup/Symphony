using System.Net;
using System.Text;
using Symphony.Core.Models;
using Symphony.Infrastructure.Tracker.GitHub;

namespace Symphony.Integration.Tests;

public sealed class GitHubTrackerClientTests
{
    [Fact]
    public async Task FetchCandidateIssuesAsync_ShouldApplyMilestoneAndLabelFilters()
    {
        const string payload = """
            {
              "data": {
                "repository": {
                  "issues": {
                    "pageInfo": {
                      "hasNextPage": false,
                      "endCursor": null
                    },
                    "nodes": [
                      {
                        "id": "I_001",
                        "number": 101,
                        "title": "Issue one",
                        "body": "Body one",
                        "state": "OPEN",
                        "url": "https://example/1",
                        "createdAt": "2026-03-05T00:00:00Z",
                        "updatedAt": "2026-03-05T01:00:00Z",
                        "milestone": { "title": "Sprint 1", "number": 1 },
                        "labels": { "nodes": [ { "name": "backend" }, { "name": "priority1" } ] },
                        "closedByPullRequestsReferences": {
                          "nodes": [
                            {
                              "id": "PR_1",
                              "number": 501,
                              "state": "OPEN",
                              "url": "https://example/pr/1",
                              "headRefName": "feature/1",
                              "baseRefName": "main"
                            }
                          ]
                        }
                      },
                      {
                        "id": "I_002",
                        "number": 102,
                        "title": "Issue two",
                        "body": "Body two",
                        "state": "OPEN",
                        "url": "https://example/2",
                        "createdAt": "2026-03-05T00:00:00Z",
                        "updatedAt": "2026-03-05T01:00:00Z",
                        "milestone": { "title": "Sprint 2", "number": 2 },
                        "labels": { "nodes": [ { "name": "frontend" } ] },
                        "closedByPullRequestsReferences": { "nodes": [] }
                      }
                    ]
                  }
                }
              }
            }
            """;

        using var httpClient = new HttpClient(new StaticJsonHandler(payload))
        {
            BaseAddress = new Uri("https://api.github.com/graphql")
        };

        var client = new GitHubTrackerClient(httpClient);
        var issues = await client.FetchCandidateIssuesAsync(new TrackerQuery(
            Endpoint: "https://api.github.com/graphql",
            ApiKey: "token",
            Owner: "released",
            Repo: "symphony",
            ActiveStates: ["Open", "In Progress"],
            Labels: ["backend"],
            Milestone: "Sprint 1"));

        var issue = Assert.Single(issues);
        Assert.Equal("#101", issue.Identifier);
        Assert.Equal("Issue one", issue.Title);
        Assert.Equal("Open", issue.State);
        Assert.Equal("Sprint 1", issue.Milestone);
        Assert.Contains("backend", issue.Labels);
        Assert.Single(issue.PullRequests);
    }

    [Theory]
    [InlineData("Closed")]
    [InlineData("Done")]
    [InlineData("Resolved")]
    [InlineData("Completed")]
    public async Task FetchIssuesByStatesAsync_ShouldReturnIssuesMatchingRequestedStates(string requestedState)
    {
        const string payload = """
            {
              "data": {
                "repository": {
                  "issues": {
                    "pageInfo": {
                      "hasNextPage": false,
                      "endCursor": null
                    },
                    "nodes": [
                      {
                        "id": "I_010",
                        "number": 110,
                        "title": "Open issue",
                        "body": "Body open",
                        "state": "OPEN",
                        "url": "https://example/10",
                        "createdAt": "2026-03-05T00:00:00Z",
                        "updatedAt": "2026-03-05T01:00:00Z",
                        "milestone": null,
                        "labels": { "nodes": [] },
                        "closedByPullRequestsReferences": { "nodes": [] }
                      },
                      {
                        "id": "I_011",
                        "number": 111,
                        "title": "Closed issue",
                        "body": "Body closed",
                        "state": "CLOSED",
                        "url": "https://example/11",
                        "createdAt": "2026-03-05T00:00:00Z",
                        "updatedAt": "2026-03-05T01:00:00Z",
                        "milestone": null,
                        "labels": { "nodes": [] },
                        "closedByPullRequestsReferences": { "nodes": [] }
                      }
                    ]
                  }
                }
              }
            }
            """;

        using var httpClient = new HttpClient(new StaticJsonHandler(payload))
        {
            BaseAddress = new Uri("https://api.github.com/graphql")
        };

        var client = new GitHubTrackerClient(httpClient);
        var issues = await client.FetchIssuesByStatesAsync(
            new TrackerQuery(
                Endpoint: "https://api.github.com/graphql",
                ApiKey: "token",
                Owner: "released",
                Repo: "symphony",
                ActiveStates: ["Open"],
                Labels: ["backend"],
                Milestone: "Sprint 1"),
            states: [requestedState]);

        var issue = Assert.Single(issues);
        Assert.Equal("#111", issue.Identifier);
        Assert.Equal("Closed", issue.State);
    }

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
