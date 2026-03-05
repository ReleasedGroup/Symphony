namespace Symphony.Core.Models;

public sealed record WorkspacePreparationRequest(
    string IssueId,
    string IssueIdentifier,
    string? SuggestedBranchName,
    string WorkspaceRoot,
    string SharedClonePath,
    string WorktreesRoot,
    string BaseBranch,
    string RemoteRepositoryUrl);
