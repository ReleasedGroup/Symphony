using Microsoft.Extensions.Options;
using Symphony.Core.Configuration;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Infrastructure.Workflows;

public sealed class WorkflowEditorService(
    WorkflowLoader loader,
    IOptions<WorkflowLoaderOptions> options,
    IWorkflowDefinitionProvider? provider = null)
{
    public const string TrackerApiKeyPlaceholder = "__SYMPHONY_KEEP_EXISTING_SECRET__";
    public const string InvalidTrackerApiKeyPlaceholderCode = "invalid_workflow_editor_tracker_api_key_placeholder";

    public async Task<WorkflowEditorDocument> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var workflowPath = WorkflowPathResolver.Resolve(options.Value.Path);
        var rawContent = await ReadWorkflowTextAsync(workflowPath, cancellationToken);
        var documentText = WorkflowEditorTextDocument.Parse(rawContent);

        var masked = WorkflowSecretMasker.Mask(documentText.FrontMatterText, documentText.PromptTemplate);
        string? effectiveRevision = null;
        if (provider is not null)
        {
            try { effectiveRevision = (await provider.GetCurrentAsync(cancellationToken)).ContentRevision; }
            catch (WorkflowLoadException) { }
        }

        try
        {
            var definition = loader.ParseContent(rawContent, workflowPath);
            return new WorkflowEditorDocument(
                workflowPath,
                definition.LoadedAtUtc,
                masked.FrontMatter,
                masked.Prompt,
                masked.HasMaskedTrackerApiKey,
                TrackerApiKeyPlaceholder,
                ValidationError: null,
                ContentRevision: WorkflowRevision.Compute(rawContent),
                EffectiveLoadedRevision: effectiveRevision ?? definition.ContentRevision);
        }
        catch (WorkflowLoadException ex)
        {
            return new WorkflowEditorDocument(
                workflowPath,
                LoadedAtUtc: null,
                masked.FrontMatter,
                masked.Prompt,
                masked.HasMaskedTrackerApiKey,
                TrackerApiKeyPlaceholder,
                SafeError(ex),
                ContentRevision: WorkflowRevision.Compute(rawContent),
                EffectiveLoadedRevision: effectiveRevision);
        }
    }

    public async Task<WorkflowEditorValidationResult> ValidateAsync(
        WorkflowEditorDocument document,
        CancellationToken cancellationToken = default)
    {
        var workflowPath = WorkflowPathResolver.Resolve(options.Value.Path);
        var current = WorkflowEditorTextDocument.Parse(await ReadWorkflowTextAsync(workflowPath, cancellationToken));
        try
        {
            var draft = ComposeRestored(document, current);
            loader.ParseContent(draft, workflowPath);
            string? effectiveRevision = null;
            if (provider is not null)
            {
                try { effectiveRevision = (await provider.GetCurrentAsync(cancellationToken)).ContentRevision; }
                catch (WorkflowLoadException) { }
            }

            return new WorkflowEditorValidationResult(true, null, WorkflowRevision.Compute(draft), effectiveRevision);
        }
        catch (WorkflowLoadException ex)
        {
            return new WorkflowEditorValidationResult(false, SafeError(ex),
                document.ContentRevision ?? string.Empty, null);
        }
    }

    public async Task<WorkflowEditorDocument> SaveAsync(
        WorkflowEditorDocument document,
        CancellationToken cancellationToken = default)
    {
        var workflowPath = WorkflowPathResolver.Resolve(options.Value.Path);
        var expectedRevision = document.ExpectedRevision ?? document.ContentRevision;
        if (string.IsNullOrWhiteSpace(expectedRevision))
        {
            throw new WorkflowLoadException("missing_workflow_revision", "A workflow revision is required to save.");
        }

        await using var writeLock = await AcquireWriteLockAsync(workflowPath, cancellationToken);
        var existingRawContent = await ReadWorkflowTextAsync(workflowPath, cancellationToken);
        var currentRevision = WorkflowRevision.Compute(existingRawContent);
        if (!string.Equals(currentRevision, expectedRevision, StringComparison.Ordinal))
        {
            throw new WorkflowEditorConflictException(expectedRevision, currentRevision);
        }

        var existingDocumentText = WorkflowEditorTextDocument.Parse(existingRawContent);
        var updatedContent = ComposeRestored(document, existingDocumentText);
        loader.ParseContent(updatedContent, workflowPath);

        var workflowDirectory = Path.GetDirectoryName(workflowPath);
        if (string.IsNullOrWhiteSpace(workflowDirectory))
        {
            workflowDirectory = Directory.GetCurrentDirectory();
        }

        Directory.CreateDirectory(workflowDirectory);

        var tempPath = Path.Combine(
            workflowDirectory,
            $".{Path.GetFileName(workflowPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllTextAsync(tempPath, updatedContent, cancellationToken);
            var latestRevision = WorkflowRevision.Compute(await ReadWorkflowTextAsync(workflowPath, cancellationToken));
            if (latestRevision != currentRevision)
            {
                throw new WorkflowEditorConflictException(expectedRevision, latestRevision);
            }
            File.Move(tempPath, workflowPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        return await GetCurrentAsync(cancellationToken);
    }

    private static string ComposeRestored(WorkflowEditorDocument document, WorkflowEditorTextDocument current)
    {
        var draftFrontMatter = NormalizeLineEndings(document.FrontMatterText);
        var draftPrompt = NormalizeLineEndings(document.PromptTemplate);
        try
        {
            var restored = WorkflowSecretMasker.Restore(draftFrontMatter, draftPrompt,
                current.FrontMatterText, current.PromptTemplate);
            return WorkflowEditorTextDocument.Compose(restored.FrontMatter, restored.Prompt);
        }
        catch (WorkflowLoadException ex) when (ex.Code == WorkflowSecretMasker.InvalidPlaceholderCode &&
                                             draftFrontMatter.Contains(TrackerApiKeyPlaceholder, StringComparison.Ordinal))
        {
            throw new WorkflowLoadException(InvalidTrackerApiKeyPlaceholderCode,
                "The tracker API key placeholder has no matching value in the current workflow.");
        }
    }

    private static WorkflowEditorValidationError SafeError(WorkflowLoadException ex)
        => new(ex.Code, "Workflow validation failed. Review the workflow configuration and prompt syntax.");

    private static async Task<FileStream> AcquireWriteLockAsync(string workflowPath, CancellationToken cancellationToken)
    {
        var lockPath = workflowPath + ".edit.lock";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private static async Task<string> ReadWorkflowTextAsync(string workflowPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(workflowPath))
        {
            throw new WorkflowLoadException("missing_workflow_file", $"Workflow file was not found at '{workflowPath}'.");
        }

        try
        {
            return await File.ReadAllTextAsync(workflowPath, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new WorkflowLoadException("missing_workflow_file", $"Workflow file could not be read at '{workflowPath}'.", ex);
        }
    }

    private static string NormalizeLineEndings(string? value)
    {
        return (value ?? string.Empty).Replace("\r\n", "\n");
    }

    internal sealed record WorkflowEditorTextDocument(
        string FrontMatterText,
        string PromptTemplate)
    {
        public static WorkflowEditorTextDocument Parse(string rawContent)
        {
            var normalized = NormalizeLineEndings(rawContent);
            if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            {
                return new WorkflowEditorTextDocument(string.Empty, normalized.Trim());
            }

            var lines = normalized.Split('\n');
            var closingIndex = -1;
            for (var index = 1; index < lines.Length; index++)
            {
                if (lines[index].Trim().Equals("---", StringComparison.Ordinal))
                {
                    closingIndex = index;
                    break;
                }
            }

            if (closingIndex < 0)
            {
                return new WorkflowEditorTextDocument(
                    string.Join('\n', lines[1..]).TrimEnd(),
                    string.Empty);
            }

            var frontMatterText = string.Join('\n', lines[1..closingIndex]).TrimEnd();
            var promptTemplate = closingIndex + 1 >= lines.Length
                ? string.Empty
                : string.Join('\n', lines[(closingIndex + 1)..]).Trim();

            return new WorkflowEditorTextDocument(frontMatterText, promptTemplate);
        }

        public static string Compose(string frontMatterText, string promptTemplate)
        {
            var normalizedFrontMatter = NormalizeLineEndings(frontMatterText).Trim();
            var normalizedPrompt = NormalizeLineEndings(promptTemplate).Trim();
            if (string.IsNullOrWhiteSpace(normalizedFrontMatter))
            {
                return string.IsNullOrWhiteSpace(normalizedPrompt)
                    ? string.Empty
                    : $"{normalizedPrompt}\n";
            }

            return string.IsNullOrWhiteSpace(normalizedPrompt)
                ? $"---\n{normalizedFrontMatter}\n---\n"
                : $"---\n{normalizedFrontMatter}\n---\n\n{normalizedPrompt}\n";
        }
    }
}
