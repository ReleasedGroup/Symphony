namespace Symphony.Core.Models;

public sealed record GitHubGraphQlExecutionResult(
    bool Success,
    string PayloadJson,
    string? ErrorCode = null,
    string? ErrorMessage = null);
