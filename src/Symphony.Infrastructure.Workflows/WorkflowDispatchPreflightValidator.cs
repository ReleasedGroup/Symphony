using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Infrastructure.Workflows;

/// <summary>
/// Validates the workflow settings required to poll GitHub and launch future agent runs.
/// </summary>
public static class WorkflowDispatchPreflightValidator
{
    /// <summary>
    /// Validates dispatch-critical settings and returns the resolved tracker API key.
    /// </summary>
    public static string ValidateAndResolveApiKey(WorkflowDefinition workflowDefinition)
    {
        ArgumentNullException.ThrowIfNull(workflowDefinition);

        if (!string.Equals(workflowDefinition.Runtime.Tracker.Kind, "github", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowLoadException(
                "unsupported_tracker_kind",
                $"Unsupported tracker.kind '{workflowDefinition.Runtime.Tracker.Kind}'. Expected 'github'.");
        }

        var apiKey = WorkflowValueResolver.ResolveApiKey(workflowDefinition.Runtime.Tracker.ApiKey);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new WorkflowLoadException(
                "missing_tracker_api_key",
                "tracker.api_key is required after environment resolution.");
        }

        if (string.IsNullOrWhiteSpace(workflowDefinition.Runtime.Tracker.Owner))
        {
            throw new WorkflowLoadException("missing_tracker_owner", "tracker.owner is required.");
        }

        if (string.IsNullOrWhiteSpace(workflowDefinition.Runtime.Tracker.Repo))
        {
            throw new WorkflowLoadException("missing_tracker_repo", "tracker.repo is required.");
        }

        if (string.IsNullOrWhiteSpace(workflowDefinition.Runtime.Codex.Command))
        {
            throw new WorkflowLoadException("invalid_codex_command", "codex.command must be non-empty.");
        }

        return apiKey;
    }
}
