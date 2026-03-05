namespace Symphony.Infrastructure.Workflows.Models;

public sealed record WorkflowDefinition(
    IReadOnlyDictionary<string, object?> Config,
    string PromptTemplate,
    WorkflowRuntimeSettings Runtime,
    string SourcePath,
    DateTimeOffset LoadedAtUtc);
