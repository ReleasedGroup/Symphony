namespace Symphony.Infrastructure.Workflows;

internal static class WorkflowValueResolver
{
    public static string? ResolveApiKey(string? rawApiKey)
    {
        if (string.IsNullOrWhiteSpace(rawApiKey))
        {
            return null;
        }

        return ResolveEnvironmentReference(rawApiKey);
    }

    public static string ResolvePathLikeValue(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return string.Empty;
        }

        var resolved = ResolveEnvironmentReference(rawValue) ?? string.Empty;
        if (resolved.Length == 0)
        {
            return string.Empty;
        }

        if (!resolved.StartsWith('~'))
        {
            return resolved;
        }

        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            return resolved;
        }

        if (resolved.Length == 1)
        {
            return homeDirectory;
        }

        var remainder = resolved[1..];
        if (remainder.StartsWith('/') || remainder.StartsWith('\\'))
        {
            return Path.Combine(homeDirectory, remainder[1..]);
        }

        return Path.Combine(homeDirectory, remainder);
    }

    private static string? ResolveEnvironmentReference(string rawValue)
    {
        var trimmed = rawValue.Trim();
        if (!trimmed.StartsWith('$'))
        {
            return trimmed;
        }

        var variableName = trimmed[1..].Trim();
        if (string.IsNullOrWhiteSpace(variableName))
        {
            return null;
        }

        var resolved = Environment.GetEnvironmentVariable(variableName);
        return string.IsNullOrWhiteSpace(resolved) ? null : resolved.Trim();
    }
}

