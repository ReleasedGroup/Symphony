namespace Symphony.Infrastructure.Workflows;

public sealed class WorkflowEditorConflictException(string expectedRevision, string currentRevision)
    : Exception("Workflow content changed since it was loaded.")
{
    public string ExpectedRevision { get; } = expectedRevision;
    public string CurrentRevision { get; } = currentRevision;
}
