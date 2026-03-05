namespace Symphony.Core.Configuration;

public sealed class WorkflowLoaderOptions
{
    public const string SectionName = "Workflow";

    public string Path { get; init; } = "WORKFLOW.md";
}
