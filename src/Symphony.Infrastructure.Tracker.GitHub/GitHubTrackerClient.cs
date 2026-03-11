using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Symphony.Core.Abstractions;
using Symphony.Core.Models;

namespace Symphony.Infrastructure.Tracker.GitHub;

public sealed partial class GitHubTrackerClient(HttpClient httpClient) : ITrackerClient, IGitHubTrackerClient
{
    private const string GraphQlIssuesQuery = """
        query($owner: String!, $repo: String!, $states: [IssueState!], $labels: [String!], $first: Int!, $after: String, $includePullRequests: Boolean!) {
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
                linkedBranches(first: 10) {
                  nodes {
                    ref {
                      name
                    }
                  }
                }
                closedByPullRequestsReferences(first: 10) @include(if: $includePullRequests) {
                  nodes {
                    id
                    number
                    state
                    url
                    headRefName
                    baseRefName
                  }
                }
                blockedBy(first: 20) {
                  nodes {
                    id
                    number
                    state
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
        if (states.Count == 0)
        {
            return [];
        }

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
            using var request = BuildGraphQlRequest(
                endpoint,
                query.ApiKey,
                GraphQlIssueStatesByIdsQuery,
                new
                {
                    ids = issueIdBatch
                });

            using var response = await SendAsync(request, cancellationToken);
            using var document = await ParseGraphQlDocumentAsync(response, cancellationToken);

            var dataElement = GetRequiredObject(document.RootElement, "data");
            var nodesElement = GetRequiredArray(dataElement, "nodes");

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

                var normalizedState = NormalizeState(GetOptionalString(issueNode, "state")) ?? "Open";
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

    public async Task<GitHubGraphQlExecutionResult> ExecuteGitHubGraphQlAsync(
        TrackerQuery query,
        string graphQlDocument,
        string? variablesJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.ApiKey))
        {
            return new GitHubGraphQlExecutionResult(
                Success: false,
                PayloadJson: "{\"error\":\"missing_tracker_auth\"}",
                ErrorCode: "missing_tracker_auth",
                ErrorMessage: "GitHub tracker auth is required.");
        }

        if (string.IsNullOrWhiteSpace(graphQlDocument))
        {
            return new GitHubGraphQlExecutionResult(
                Success: false,
                PayloadJson: "{\"error\":\"invalid_graphql_document\"}",
                ErrorCode: "invalid_graphql_document",
                ErrorMessage: "GraphQL query must be non-empty.");
        }

        if (!ContainsSingleGraphQlOperation(graphQlDocument))
        {
            return new GitHubGraphQlExecutionResult(
                Success: false,
                PayloadJson: "{\"error\":\"invalid_graphql_document\"}",
                ErrorCode: "invalid_graphql_document",
                ErrorMessage: "GraphQL document must contain exactly one operation.");
        }

        JsonNode? variablesNode = null;
        if (!string.IsNullOrWhiteSpace(variablesJson))
        {
            try
            {
                variablesNode = JsonNode.Parse(variablesJson);
            }
            catch (JsonException ex)
            {
                return new GitHubGraphQlExecutionResult(
                    Success: false,
                    PayloadJson: "{\"error\":\"invalid_graphql_variables\"}",
                    ErrorCode: "invalid_graphql_variables",
                    ErrorMessage: ex.Message);
            }

            if (variablesNode is not JsonObject)
            {
                return new GitHubGraphQlExecutionResult(
                    Success: false,
                    PayloadJson: "{\"error\":\"invalid_graphql_variables\"}",
                    ErrorCode: "invalid_graphql_variables",
                    ErrorMessage: "GraphQL variables must be a JSON object.");
            }
        }

        var endpoint = string.IsNullOrWhiteSpace(query.Endpoint) ? "https://api.github.com/graphql" : query.Endpoint;

        try
        {
            using var request = BuildGraphQlRequest(
                endpoint,
                query.ApiKey,
                graphQlDocument,
                variablesNode);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var payloadJson = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new GitHubGraphQlExecutionResult(
                    Success: false,
                    PayloadJson: string.IsNullOrWhiteSpace(payloadJson)
                        ? $"{{\"error\":\"github_api_status\",\"status\":{(int)response.StatusCode}}}"
                        : payloadJson,
                    ErrorCode: "github_api_status",
                    ErrorMessage: $"GitHub GraphQL returned HTTP {(int)response.StatusCode}.");
            }

            using var payloadDocument = JsonDocument.Parse(payloadJson);
            var success = !(payloadDocument.RootElement.TryGetProperty("errors", out var errorsElement) &&
                            errorsElement.ValueKind == JsonValueKind.Array &&
                            errorsElement.GetArrayLength() > 0);

            return new GitHubGraphQlExecutionResult(
                Success: success,
                PayloadJson: payloadJson,
                ErrorCode: success ? null : "github_graphql_errors",
                ErrorMessage: success ? null : "GitHub GraphQL returned errors.");
        }
        catch (JsonException ex)
        {
            return new GitHubGraphQlExecutionResult(
                Success: false,
                PayloadJson: "{\"error\":\"github_unknown_payload\"}",
                ErrorCode: "github_unknown_payload",
                ErrorMessage: ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GitHubGraphQlExecutionResult(
                Success: false,
                PayloadJson: "{\"error\":\"github_api_request\"}",
                ErrorCode: "github_api_request",
                ErrorMessage: "GitHub GraphQL request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return new GitHubGraphQlExecutionResult(
                Success: false,
                PayloadJson: "{\"error\":\"github_api_request\"}",
                ErrorCode: "github_api_request",
                ErrorMessage: ex.Message);
        }
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
            using var request = BuildGraphQlRequest(
                endpoint,
                query.ApiKey,
                GraphQlIssuesQuery,
                new
                {
                    owner = query.Owner,
                    repo = query.Repo,
                    states = issueStates,
                    labels = applyCandidateFilters && query.Labels.Count != 0 ? query.Labels : null,
                    includePullRequests = query.IncludePullRequests,
                    first = query.PageSize <= 0 ? 50 : query.PageSize,
                    after = cursor
                });

            using var response = await SendAsync(request, cancellationToken);
            using var document = await ParseGraphQlDocumentAsync(response, cancellationToken);

            var dataElement = GetRequiredObject(document.RootElement, "data");
            var repositoryElement = GetRequiredObject(dataElement, "repository");
            var issuesElement = GetRequiredObject(repositoryElement, "issues");
            var nodesElement = GetRequiredArray(issuesElement, "nodes");

            foreach (var issueNode in nodesElement.EnumerateArray())
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

            var pageInfo = GetRequiredObject(issuesElement, "pageInfo");
            hasNextPage = GetRequiredBoolean(pageInfo, "hasNextPage");
            if (!hasNextPage)
            {
                cursor = null;
                continue;
            }

            if (!pageInfo.TryGetProperty("endCursor", out var endCursor))
            {
                throw new GitHubTrackerException(
                    "github_missing_end_cursor",
                    "GitHub GraphQL pagination payload is missing endCursor.");
            }

            cursor = endCursor.GetString();
            if (string.IsNullOrWhiteSpace(cursor))
            {
                throw new GitHubTrackerException(
                    "github_missing_end_cursor",
                    "GitHub GraphQL pagination payload contained an empty endCursor.");
            }
        }

        return candidateIssues;
    }

    private static NormalizedIssue ParseIssue(JsonElement issueNode, bool includePullRequests)
    {
        var labels = issueNode.TryGetProperty("labels", out var labelsNode) &&
                     labelsNode.ValueKind == JsonValueKind.Object &&
                     labelsNode.TryGetProperty("nodes", out var labelNodes) &&
                     labelNodes.ValueKind == JsonValueKind.Array
            ? labelNodes
                .EnumerateArray()
                .Select(node => GetOptionalString(node, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        var blockedBy = issueNode.TryGetProperty("blockedBy", out var blockedByNode) &&
                        blockedByNode.ValueKind == JsonValueKind.Object &&
                        blockedByNode.TryGetProperty("nodes", out var blockerNodes) &&
                        blockerNodes.ValueKind == JsonValueKind.Array
            ? blockerNodes
                .EnumerateArray()
                .Select(node =>
                {
                    var number = GetOptionalInt(node, "number");
                    return new BlockerRef(
                        GetOptionalString(node, "id"),
                        number.HasValue ? $"#{number.Value}" : null,
                        NormalizeState(GetOptionalString(node, "state")));
                })
                .ToList()
            : [];

        var pullRequests = includePullRequests &&
                           issueNode.TryGetProperty("closedByPullRequestsReferences", out var pullRequestReferencesNode) &&
                           pullRequestReferencesNode.ValueKind != JsonValueKind.Null &&
                           pullRequestReferencesNode.TryGetProperty("nodes", out var pullRequestNodes) &&
                           pullRequestNodes.ValueKind == JsonValueKind.Array
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

        var normalizedState = NormalizeState(GetOptionalString(issueNode, "state")) ?? "Open";
        var number = GetOptionalInt(issueNode, "number");
        var identifier = number is null ? GetOptionalString(issueNode, "id") ?? "unknown" : $"#{number.Value}";
        var branchName = GetLinkedBranchName(issueNode) ?? pullRequests.FirstOrDefault()?.HeadRef;

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
            BlockedBy: blockedBy,
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
            if (IssueStateMatcher.IsClosedState(state))
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
        return IssueStateMatcher.MatchesConfiguredActiveState(issueState, configuredStates);
    }

    private static string? GetLinkedBranchName(JsonElement issueNode)
    {
        if (!issueNode.TryGetProperty("linkedBranches", out var linkedBranchesNode) ||
            linkedBranchesNode.ValueKind != JsonValueKind.Object ||
            !linkedBranchesNode.TryGetProperty("nodes", out var branchNodes) ||
            branchNodes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var node in branchNodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object ||
                !node.TryGetProperty("ref", out var refNode) ||
                refNode.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var branchName = GetOptionalString(refNode, "name");
            if (!string.IsNullOrWhiteSpace(branchName))
            {
                return branchName;
            }
        }

        return null;
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

    private static HttpRequestMessage BuildGraphQlRequest(
        string endpoint,
        string apiKey,
        string graphQlQuery,
        object? variables)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Symphony", "1.0"));
        request.Content = JsonContent.Create(new
        {
            query = graphQlQuery,
            variables
        });

        return request;
    }

    private static bool ContainsSingleGraphQlOperation(string graphQlDocument)
    {
        var stripped = StripGraphQlCommentsAndStrings(graphQlDocument);
        if (string.IsNullOrWhiteSpace(stripped))
        {
            return false;
        }

        var operationCount = 0;
        var depth = 0;
        var awaitingExplicitOperationBody = false;

        for (var index = 0; index < stripped.Length; index++)
        {
            var current = stripped[index];
            switch (current)
            {
                case '{':
                    if (depth == 0)
                    {
                        if (awaitingExplicitOperationBody)
                        {
                            awaitingExplicitOperationBody = false;
                        }
                        else
                        {
                            operationCount++;
                        }
                    }

                    depth++;
                    break;
                case '}':
                    depth = Math.Max(depth - 1, 0);
                    break;
                default:
                    if (depth != 0 || !char.IsLetter(current))
                    {
                        break;
                    }

                    var start = index;
                    while (index < stripped.Length && (char.IsLetter(stripped[index]) || stripped[index] == '_'))
                    {
                        index++;
                    }

                    var token = stripped[start..index];
                    if (token is "query" or "mutation" or "subscription")
                    {
                        operationCount++;
                        awaitingExplicitOperationBody = true;
                    }

                    index--;
                    break;
            }

            if (operationCount > 1)
            {
                return false;
            }
        }

        return operationCount == 1;
    }

    private static string StripGraphQlCommentsAndStrings(string input)
    {
        var chars = new List<char>(input.Length);
        var inString = false;
        var inComment = false;
        var escapeNext = false;

        foreach (var current in input)
        {
            if (inComment)
            {
                if (current is '\r' or '\n')
                {
                    inComment = false;
                    chars.Add(current);
                }

                continue;
            }

            if (inString)
            {
                if (escapeNext)
                {
                    escapeNext = false;
                    continue;
                }

                if (current == '\\')
                {
                    escapeNext = true;
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '#')
            {
                inComment = true;
                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            chars.Add(current);
        }

        return new string(chars.ToArray());
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new GitHubTrackerException(
                    "github_api_status",
                    $"GitHub GraphQL returned HTTP {(int)response.StatusCode}.");
            }

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubTrackerException("github_api_request", "GitHub GraphQL request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubTrackerException("github_api_request", "GitHub GraphQL request failed.", ex);
        }
    }

    private static async Task<JsonDocument> ParseGraphQlDocumentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: cancellationToken);

            if (document.RootElement.TryGetProperty("errors", out var errorsElement) &&
                errorsElement.ValueKind == JsonValueKind.Array &&
                errorsElement.GetArrayLength() > 0)
            {
                document.Dispose();
                throw new GitHubTrackerException("github_graphql_errors", "GitHub GraphQL returned errors.");
            }

            return document;
        }
        catch (GitHubTrackerException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new GitHubTrackerException("github_unknown_payload", "GitHub GraphQL payload was not valid JSON.", ex);
        }
    }

    private static JsonElement GetRequiredObject(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            throw new GitHubTrackerException(
                "github_unknown_payload",
                $"GitHub GraphQL payload is missing object property '{propertyName}'.");
        }

        return property;
    }

    private static JsonElement GetRequiredArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            throw new GitHubTrackerException(
                "github_unknown_payload",
                $"GitHub GraphQL payload is missing array property '{propertyName}'.");
        }

        return property;
    }

    private static bool GetRequiredBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new GitHubTrackerException(
                "github_unknown_payload",
                $"GitHub GraphQL payload is missing boolean property '{propertyName}'.");
        }

        return property.GetBoolean();
    }

    private static string? NormalizeState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return null;
        }

        return IssueStateMatcher.IsClosedState(state) ? "Closed" : "Open";
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
