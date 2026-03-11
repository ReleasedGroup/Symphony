using Microsoft.Data.Sqlite;

namespace Symphony.Infrastructure.Persistence.Sqlite.Storage;

internal static class SqliteConnectionStringResolver
{
    public static string Resolve(string connectionString, string? contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (!TryResolveFilePath(builder.DataSource, contentRootPath, out var resolvedPath))
        {
            return connectionString;
        }

        var parentDirectory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        builder.DataSource = resolvedPath;
        return builder.ToString();
    }

    private static bool TryResolveFilePath(string? dataSource, string? contentRootPath, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            return false;
        }

        var trimmed = dataSource.Trim();
        if (trimmed.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var basePath = string.IsNullOrWhiteSpace(contentRootPath)
            ? Directory.GetCurrentDirectory()
            : contentRootPath;

        resolvedPath = Path.IsPathRooted(trimmed)
            ? Path.GetFullPath(trimmed)
            : Path.GetFullPath(Path.Combine(basePath, trimmed));

        return true;
    }
}
