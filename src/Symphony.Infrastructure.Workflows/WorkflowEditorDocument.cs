namespace Symphony.Infrastructure.Workflows;

public sealed record WorkflowEditorDocument(
    string SourcePath,
    DateTimeOffset? LoadedAtUtc,
    string FrontMatterText,
    string PromptTemplate,
    bool HasMaskedTrackerApiKey,
    string TrackerApiKeyPlaceholder,
    WorkflowEditorValidationError? ValidationError,
    string? ContentRevision = null,
    string? EffectiveLoadedRevision = null,
    string? ExpectedRevision = null);

public sealed record WorkflowEditorValidationError(
    string Code,
    string Message);

public sealed record WorkflowEditorValidationResult(
    bool Valid,
    WorkflowEditorValidationError? Error,
    string ContentRevision,
    string? EffectiveLoadedRevision);
