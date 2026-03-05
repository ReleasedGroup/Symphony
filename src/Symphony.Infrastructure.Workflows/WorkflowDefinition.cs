namespace Symphony.Infrastructure.Workflows;

public sealed record WorkflowDefinition(IReadOnlyDictionary<string, object?> Config, string PromptTemplate);
