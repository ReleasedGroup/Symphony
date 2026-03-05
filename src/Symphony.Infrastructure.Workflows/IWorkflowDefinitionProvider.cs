using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Infrastructure.Workflows;

public interface IWorkflowDefinitionProvider
{
    Task<WorkflowDefinition> GetCurrentAsync(CancellationToken cancellationToken = default);
}
