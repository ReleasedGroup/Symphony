using Symphony.Core.Models;

namespace Symphony.Core.Abstractions;

public interface IWorkspaceManager
{
    Task<WorkspacePreparationResult> PrepareIssueWorkspaceAsync(
        WorkspacePreparationRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkspaceCleanupResult> CleanupIssueWorkspaceAsync(
        WorkspaceCleanupRequest request,
        CancellationToken cancellationToken = default);
}
