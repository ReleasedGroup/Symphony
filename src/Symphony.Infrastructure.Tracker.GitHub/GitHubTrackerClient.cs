using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Symphony.Core.Models;

namespace Symphony.Infrastructure.Tracker.GitHub;

public sealed partial class GitHubTrackerClient(HttpClient httpClient) : IGitHubTrackerClient
{
    private const string GraphQlIssuesQuery = """
        query($owner: String!, $repo: String!, $states: [IssueState!], $labels: [String!], $first: Int!, $after: String) {
          repository(owner: $owner, name: $repo) {
            issues(states: $states, labels: $labels, first: $first, after: $after, orderBy: { field: CREATED_AT, direction: ASC }) {
              pageInfo {
                hasNextPage
                endCursor
              }
              nodes {
                id
                number
                title
                body
                state
                url
                createdAt
                updatedAt
                milestone {
                  title
                  number
                }
                labels(first: 50) {
                  nodes {
                    name
                  }
                }
                closedByPullRequestsReferences(first: 10) {
                  nodes {
                    id
                    number
                    state
                    url
                    headRefName
                    baseRefName
                  }
                }
              }
            }
          }
        }
        """;

    private const string GraphQlIssueStatesByIdsQuery = """
        query($ids: [ID!]!) {
          nodes(ids: $ids) {
            ... on Issue {
              id
              state
              repository {
                name
                owner {
                  login
                }
              }
            }
          }
        }
        """;

    public async Task<IReadOnlyList<NormalizedIssue>> FetchCandidateIssuesAsync(
        TrackerQuery query,
        CancellationToken cancellationToken = default)
    {
        return await FetchIssuesInternalAsync(
            query,
            states: query.ActiveStates,
            applyCandidateFilters: true,
            cancellationToken);
    }

    public async Task<IReadOnlyList<NormalizedIssue>> FetchIssuesByStatesAsync(
        TrackerQuery query,
        IReadOnlyList<string> states,
        CancellationToken cancellationToken = default)
    {
        return await FetchIssuesInternalAsync(
            query,
            states,
            applyCandidateFilters: false,
            cancellationToken);
    }

    public async Task<IReadOnlyList<IssueStateSnapshot>> FetchIssueStatesByIdsAsync(
        TrackerQuery query,
        IReadOnlyList<string> issueIds,
        CancellationToken cancellationToken = default)
    {
        if (issueIds.Count == 0)
        {
            return [];
        }

        var endpoint = string.IsNullOrWhiteSpace(query.Endpoint) ? "https://api.github.com/graphql" : query.Endpoint;
        var orderedIds = issueIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var statesById = new Dictionary<string, IssueStateSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var issueIdBatch in orderedIds.Chunk(100))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", query.ApiKey);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Symphony", "1.0"));
            request.Content = JsonContent.Create(new
            {
                query = GraphQlIssueStatesByIdsQuery,
                variables = new
                {
                    ids = issueIdBatch
                }
            });

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"github_api_status: {(int)response.StatusCode}");
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);

            if (document.RootElement.TryGetProperty("errors", out var errorsElement) &&
                errorsElement.ValueKind == JsonValueKind.Array &&
                errorsElement.GetArrayLength() > 0)
            {
                throw new InvalidOperationException("github_graphql_errors");
            }

            if (!document.RootElement.TryGetProperty("data", out var dataElement) ||
                !dataElement.TryGetProperty("nodes", out var nodesElement) ||
                nodesElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("github_unknown_payload");
            }

            foreach (var issueNode in nodesElement.EnumerateArray())
            {
                if (issueNode.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!issueNode.TryGetProperty("repository", out var repositoryNode) ||
                    repositoryNode.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var owner = repositoryNode.TryGetProperty("owner", out var ownerNode)
                    ? GetOptionalString(ownerNode, "login")
                    : null;
                var repo = GetOptionalString(repositoryNode, "name");

                if (!string.Equals(owner, query.Owner, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(repo, query.Repo, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var issueId = GetOptionalString(issueNode, "id");
                if (string.IsNullOrWhiteSpace(issueId))
                {
                    continue;
                }

                var issueState = GetOptionalString(issueNode, "state") ?? "OPEN";
                var normalizedState = issueState.Equals("CLOSED", StringComparison.OrdinalIgnoreCase) ? "Closed" : "Open";
                statesById[issueId] = new IssueStateSnapshot(issueId, normalizedState);
            }
        }

        var result = new List<IssueStateSnapshot>(statesById.Count);
        foreach (var issueId in orderedIds)
        {
            if (statesById.TryGetValue(issueId, out var state))
            {
                result.Add(state);
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<NormalizedIssue>> FetchIssuesInternalAsync(
        TrackerQuery query,
        IReadOnlyList<string> states,
        bool applyCandidateFilters,
        CancellationToken cancellationToken)
    {
        var candidateIssues = new List<NormalizedIssue>();
        var cursor = default(string);
        var hasNextPage = true;

        var issueStates = BuildIssueStates(states);
        var endpoint = string.IsNullOrWhiteSpace(query.Endpoint) ? "https://api.github.com/graphql" : query.Endpoint;

        while (hasNextPage)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", query.ApiKey);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Symphony", "1.0"));
            request.Content = JsonContent.Create(new
            {
                query = GraphQlIssuesQuery,
                variables = new
                {
                    owner = query.Owner,
                    repo = query.Repo,
                    states = issueStates,
                    labels = applyCandidateFilters && query.Labels.Count != 0 ? query.Labels : null,
                    first = query.PageSize <= 0 ? 50 : query.PageSize,
                    after = cursor
                }
            });

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"github_api_status: {(int)response.StatusCode}");
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);

            if (document.RootElement.TryGetProperty("errors", out var errorsElement) &&
                errorsElement.ValueKind == JsonValueKind.Array &&
                errorsElement.GetArrayLength() > 0)
            {
                throw new InvalidOperationException("github_graphql_errors");
            }

            var issuesElement = document.RootElement
                .GetProperty("data")
                .GetProperty("repository")
                .GetProperty("issues");

            foreach (var issueNode in issuesElement.GetProperty("nodes").EnumerateArray())
            {
                var issue = ParseIssue(issueNode, query.IncludePullRequests);

                if (applyCandidateFilters && !MatchesMilestone(issue.Milestone, issueNode, query.Milestone))
                {
                    continue;
                }

                if (applyCandidateFilters && !MatchesLabels(issue.Labels, query.Labels))
                {
                    continue;
                }

                if (!MatchesActiveState(issue.State, states))
                {
                    continue;
                }

                candidateIssues.Add(issue);
            }

            var pageInfo = issuesElement.GetProperty("pageInfo");
            hasNextPage = pageInfo.GetProperty("hasNextPage").GetBoolean();
            if (!hasNextPage)
            {
                cursor = null;
                continue;
            }

            if (!pageInfo.TryGetProperty("endCursor", out var endCursor))
            {
                throw new InvalidOperationException("github_missing_end_cursor");
            }

            cursor = endCursor.GetString();
            if (string.IsNullOrWhiteSpace(cursor))
            {
                throw new InvalidOperationException("github_missing_end_cursor");
            }
        }

        return candidateIssues;
    }

    private static NormalizedIssue ParseIssue(JsonElement issueNode, bool includePullRequests)
    {
        var labels = issueNode
            .GetProperty("labels")
            .GetProperty("nodes")
            .EnumerateArray()
            .Select(node => node.GetProperty("name").GetString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pullRequests = includePullRequests
            ? issueNode
                .GetProperty("closedByPullRequestsReferences")
                .GetProperty("nodes")
                .EnumerateArray()
                .Select(node => new PullRequestRef(
                    GetOptionalString(node, "id"),
                    GetOptionalInt(node, "number"),
                    GetOptionalString(node, "state"),
                    GetOptionalString(node, "url"),
                    GetOptionalString(node, "headRefName"),
                    GetOptionalString(node, "baseRefName")))
                .ToList()
            : [];

        var milestoneTitle = issueNode.TryGetProperty("milestone", out var milestoneNode) &&
                             milestoneNode.ValueKind != JsonValueKind.Null &&
                             milestoneNode.TryGetProperty("title", out var milestoneTitleNode)
            ? milestoneTitleNode.GetString()
            : null;

        var issueState = GetOptionalString(issueNode, "state") ?? "OPEN";
        var normalizedState = issueState.Equals("CLOSED", StringComparison.OrdinalIgnoreCase) ? "Closed" : "Open";
        var number = GetOptionalInt(issueNode, "number");
        var identifier = number is null ? GetOptionalString(issueNode, "id") ?? "unknown" : $"#{number.Value}";
        var branchName = includePullRequests ? pullRequests.FirstOrDefault()?.HeadRef : null;

        return new NormalizedIssue(
            Id: GetOptionalString(issueNode, "id") ?? Guid.NewGuid().ToString("N"),
            Identifier: identifier,
            Title: GetOptionalString(issueNode, "title") ?? "(untitled issue)",
            Description: GetOptionalString(issueNode, "body"),
            Priority: InferPriority(labels),
            State: normalizedState,
            BranchName: branchName,
            Url: GetOptionalString(issueNode, "url"),
            Milestone: milestoneTitle,
            Labels: labels,
            PullRequests: pullRequests,
            CreatedAt: ParseDateTimeOffset(issueNode, "createdAt"),
            UpdatedAt: ParseDateTimeOffset(issueNode, "updatedAt"));
    }

    private static IReadOnlyList<string> BuildIssueStates(IReadOnlyList<string> activeStates)
    {
        if (activeStates.Count == 0)
        {
            return ["OPEN"];
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in activeStates)
        {
            if (IsClosedState(state))
            {
                result.Add("CLOSED");
            }
            else
            {
                result.Add("OPEN");
            }
        }

        return result.Count == 0 ? ["OPEN"] : result.ToList();
    }

    private static bool MatchesMilestone(string? milestoneTitle, JsonElement issueNode, string? configuredMilestone)
    {
        if (string.IsNullOrWhiteSpace(configuredMilestone))
        {
            return true;
        }

        if (string.Equals(milestoneTitle, configuredMilestone, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!issueNode.TryGetProperty("milestone", out var milestoneNode) || milestoneNode.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        var milestoneNumber = milestoneNode.TryGetProperty("number", out var numberNode)
            ? numberNode.GetInt32().ToString()
            : null;

        return string.Equals(milestoneNumber, configuredMilestone, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesLabels(IReadOnlyList<string> issueLabels, IReadOnlyList<string> requestedLabels)
    {
        if (requestedLabels.Count == 0)
        {
            return true;
        }

        var issueLabelSet = new HashSet<string>(issueLabels, StringComparer.OrdinalIgnoreCase);
        return requestedLabels.All(label => issueLabelSet.Contains(label));
    }

    private static bool MatchesActiveState(string issueState, IReadOnlyList<string> configuredStates)
    {
        if (configuredStates.Count == 0)
        {
            return issueState.Equals("Open", StringComparison.OrdinalIgnoreCase);
        }

        if (issueState.Equals("Closed", StringComparison.OrdinalIgnoreCase))
        {
            return configuredStates.Any(IsClosedState);
        }

        return configuredStates.Any(state => !IsClosedState(state));
    }

    private static bool IsClosedState(string state)
    {
        var normalized = state.Trim().ToLowerInvariant();
        return normalized is "closed" or "done" or "resolved" or "completed";
    }

    private static int? InferPriority(IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            var match = PriorityRegex().Match(label);
            if (match.Success && int.TryParse(match.Groups["priority"].Value, out var priority))
            {
                return priority;
            }
        }

        return null;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString()
            : null;
    }

    private static int? GetOptionalInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : null;
    }

    private static DateTimeOffset? ParseDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(property.GetString(), out var parsed)
            ? parsed
            : null;
    }

    [GeneratedRegex(@"(?:^|[\s:_-])p(?:riority)?(?<priority>[1-4])(?:$|[\s:_-])", RegexOptions.IgnoreCase)]
    private static partial Regex PriorityRegex();
}
