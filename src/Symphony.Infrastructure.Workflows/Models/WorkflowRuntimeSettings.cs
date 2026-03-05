namespace Symphony.Infrastructure.Workflows.Models;

public sealed record WorkflowRuntimeSettings(
    WorkflowTrackerSettings Tracker,
    WorkflowPollingSettings Polling,
    WorkflowAgentSettings Agent,
    WorkflowWorkspaceSettings Workspace,
    WorkflowCodexSettings Codex);

public sealed record WorkflowTrackerSettings(
    string Kind,
    string Endpoint,
    string ApiKey,
    string Owner,
    string Repo,
    string? Milestone,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> ActiveStates,
    IReadOnlyList<string> TerminalStates);

public sealed record WorkflowPollingSettings(int IntervalMs);

public sealed record WorkflowAgentSettings(int MaxConcurrentAgents);

public sealed record WorkflowWorkspaceSettings(
    string Root,
    string SharedClonePath,
    string WorktreesRoot,
    string BaseBranch,
    string? RemoteUrl);

public sealed record WorkflowCodexSettings(
    string Command,
    int TimeoutMs);
