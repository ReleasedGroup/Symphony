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

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(rootWithSeparator, comparison) &&
            !candidate.Equals(root, comparison))
        {
            throw new InvalidOperationException($"Path '{candidatePath}' must be within root '{rootPath}'.");
        }
        EnsureNoReparsePoints(candidate);
    }

    public static void EnsureNoReparsePoints(string path)
    {
        for (var current = new DirectoryInfo(GetAbsolutePath(path)); current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0 && current.Attributes != (FileAttributes)(-1))
            {
                throw new InvalidOperationException($"Workspace path must not traverse a symbolic link or junction: '{current.FullName}'.");
            }
        }
    }

    public static string SanitizeIssueIdentifier(string issueIdentifier)
    {
        var input = string.IsNullOrWhiteSpace(issueIdentifier) ? "issue" : issueIdentifier.Trim();
        var sanitized = UnsafeWorkspaceNameCharactersRegex().Replace(input, "_").Trim('_');
        if (sanitized is "." or "..")
        {
            throw new InvalidOperationException("Issue identifier must name a workspace, not its parent.");
        }
        return string.IsNullOrWhiteSpace(sanitized) ? "issue" : sanitized.ToLowerInvariant();
    }

    [GeneratedRegex(@"[^a-zA-Z0-9._-]+")]
    private static partial Regex UnsafeWorkspaceNameCharactersRegex();
}
