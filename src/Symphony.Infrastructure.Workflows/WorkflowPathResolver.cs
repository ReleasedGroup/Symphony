using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Infrastructure.Workflows;

/// <summary>
/// Resolves the effective workflow file path using Symphony's runtime path rules.
/// </summary>
public static class WorkflowPathResolver
{
    public static string Resolve(string? configuredPath)
    {
        var useDefaultPath = string.IsNullOrWhiteSpace(configuredPath);
        var requestedPath = useDefaultPath
            ? "WORKFLOW.md"
            : configuredPath!;

        var resolvedPath = WorkflowValueResolver.ResolvePathLikeValue(requestedPath);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            if (!useDefaultPath && requestedPath.TrimStart().StartsWith('$'))
            {
                throw new WorkflowLoadException(
                    "missing_workflow_file",
                    $"Workflow path environment reference '{requestedPath}' did not resolve to a value.");
            }

            resolvedPath = "WORKFLOW.md";
        }

        if (Path.IsPathRooted(resolvedPath))
        {
            return Path.GetFullPath(resolvedPath);
        }

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), resolvedPath));
    }
}
