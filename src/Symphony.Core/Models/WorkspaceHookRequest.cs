namespace Symphony.Core.Models;

public sealed record WorkspaceHookRequest(
    string HookName,
    string Script,
    string WorkspacePath,
    int TimeoutMs,
    string IssueIdentifier);
