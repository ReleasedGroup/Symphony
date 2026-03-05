namespace Symphony.Core.Models;

public sealed record WorkspaceCleanupResult(
    string WorkspacePath,
    bool Existed,
    bool RemovedNow);
