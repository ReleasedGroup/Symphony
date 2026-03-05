namespace Symphony.Infrastructure.Workflows;

public sealed class WorkflowLoader
{
    public Task<WorkflowDefinition> LoadAsync(string workflowPath, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new WorkflowDefinition(new Dictionary<string, object?>(), $"Loaded from: {workflowPath}"));
    }
}
