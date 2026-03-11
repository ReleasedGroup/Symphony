namespace Symphony.Core.Models;

public sealed record AgentRunRequest(
    string IssueId,
    string IssueIdentifier,
    string IssueTitle,
    string WorkspacePath,
    string Prompt,
    string Command,
    int TimeoutMs,
    int MaxTurns,
    string ApprovalPolicy,
    string ThreadSandbox,
    string TurnSandboxPolicy,
    int ReadTimeoutMs,
    TrackerQuery? TrackerQuery = null);
