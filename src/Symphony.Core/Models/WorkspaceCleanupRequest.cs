namespace Symphony.Core.Models;

public sealed record WorkspaceCleanupRequest(
    string IssueIdentifier,
    string WorkspaceRoot,
    string SharedClonePath,
    string WorktreesRoot,
    string? BeforeRemoveHook,
    int HookTimeoutMs);
