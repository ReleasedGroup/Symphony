using System.Text.RegularExpressions;

namespace Symphony.Core.Models;

public static partial class SecretRedactor
{
    public static string? Redact(string? value, params string?[] knownSecrets)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var redacted = BearerTokenRegex().Replace(value, "Bearer ***");
        redacted = GitHubTokenRegex().Replace(redacted, "***");
        redacted = AuthorizationHeaderRegex().Replace(redacted, "${prefix}***");

        foreach (var secret in knownSecrets)
        {
            if (string.IsNullOrWhiteSpace(secret) || secret.Length < 4)
            {
                continue;
            }

            redacted = redacted.Replace(secret, "***", StringComparison.Ordinal);
        }

        return redacted;
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9_\-\.=]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"github_pat_[A-Za-z0-9_]+|gh[opusr]_[A-Za-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"(?<prefix>(?:authorization|x-auth-token)\s*[:=]\s*)(?<value>\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeaderRegex();
}
