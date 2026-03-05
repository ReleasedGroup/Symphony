namespace Symphony.Core.Models;

public sealed record WorkspacePreparationResult(
    string WorkspacePath,
    string BranchName,
    bool CreatedNow);
