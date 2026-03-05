namespace Symphony.Core.Models;

public sealed record AgentRunRequest(
    string IssueId,
    string IssueIdentifier,
    string WorkspacePath,
    string Prompt,
    string Command,
    int TimeoutMs);
