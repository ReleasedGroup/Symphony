using Symphony.Core.Models;

namespace Symphony.Core.Abstractions;

public interface IWorkspaceHookRunner
{
    Task RunHookAsync(
        WorkspaceHookRequest request,
        CancellationToken cancellationToken = default);
}
