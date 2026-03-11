using System.Text.RegularExpressions;

namespace Symphony.Core.Models;

public static partial class WorkspacePathSafety
{
    public static string GetAbsolutePath(string path)
    {
        return Path.GetFullPath(path);
    }

    public static void EnsurePathIsWithinRoot(string rootPath, string candidatePath)
    {
        var root = GetAbsolutePath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = GetAbsolutePath(candidatePath);
        var rootWithSeparator = root + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
            !candidate.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path '{candidatePath}' must be within root '{rootPath}'.");
        }
    }

    public static string SanitizeIssueIdentifier(string issueIdentifier)
    {
        var input = string.IsNullOrWhiteSpace(issueIdentifier) ? "issue" : issueIdentifier.Trim();
        var sanitized = UnsafeWorkspaceNameCharactersRegex().Replace(input, "_").Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? "issue" : sanitized.ToLowerInvariant();
    }

    [GeneratedRegex(@"[^a-zA-Z0-9._-]+")]
    private static partial Regex UnsafeWorkspaceNameCharactersRegex();
}
