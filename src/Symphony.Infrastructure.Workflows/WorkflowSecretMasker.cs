using System.Text.RegularExpressions;

namespace Symphony.Infrastructure.Workflows;

internal static partial class WorkflowSecretMasker
{
    internal const string InvalidPlaceholderCode = "invalid_workflow_editor_secret_placeholder";

    public static (string FrontMatter, string Prompt, bool HasMaskedTrackerApiKey) Mask(
        string frontMatter,
        string prompt)
    {
        var replacements = Collect(frontMatter, prompt);
        var maskedFrontMatter = frontMatter;
        var maskedPrompt = prompt;
        foreach (var (value, placeholder) in replacements)
        {
            maskedFrontMatter = maskedFrontMatter.Replace(value, placeholder, StringComparison.Ordinal);
            maskedPrompt = maskedPrompt.Replace(value, placeholder, StringComparison.Ordinal);
        }

        return (maskedFrontMatter, maskedPrompt,
            replacements.Any(item => item.Placeholder == WorkflowEditorService.TrackerApiKeyPlaceholder));
    }

    public static (string FrontMatter, string Prompt) Restore(
        string draftFrontMatter,
        string draftPrompt,
        string currentFrontMatter,
        string currentPrompt)
    {
        var maskedCurrent = Mask(currentFrontMatter, currentPrompt);
        ValidatePlaceholderPlacement(draftFrontMatter, maskedCurrent.FrontMatter, "front matter", yaml: true);
        ValidatePlaceholderPlacement(draftPrompt, maskedCurrent.Prompt, "prompt", yaml: false);

        foreach (var (value, placeholder) in Collect(currentFrontMatter, currentPrompt))
        {
            draftFrontMatter = draftFrontMatter.Replace(placeholder, value, StringComparison.Ordinal);
            draftPrompt = draftPrompt.Replace(placeholder, value, StringComparison.Ordinal);
        }

        if (PlaceholderRegex().IsMatch(draftFrontMatter) || PlaceholderRegex().IsMatch(draftPrompt))
        {
            throw new Models.WorkflowLoadException(
                InvalidPlaceholderCode,
                "A secret placeholder has no matching value in the current workflow.");
        }

        return (draftFrontMatter, draftPrompt);
    }

    private static void ValidatePlaceholderPlacement(string draft, string originalMasked, string field, bool yaml)
    {
        var permittedLines = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var placement in GetPlaceholderPlacements(originalMasked, yaml))
        {
            permittedLines[placement] = permittedLines.GetValueOrDefault(placement) + 1;
        }

        foreach (var placement in GetPlaceholderPlacements(draft, yaml))
        {
            if (!permittedLines.TryGetValue(placement, out var remaining) || remaining == 0)
            {
                throw new Models.WorkflowLoadException(InvalidPlaceholderCode,
                    $"A secret placeholder moved or changed in the {field}.");
            }
            permittedLines[placement] = remaining - 1;
        }
    }

    private static IEnumerable<string> GetPlaceholderPlacements(string text, bool yaml)
    {
        var parents = new List<(int Indent, string Key)>();
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = yaml ? SecretYamlLineRegex().Match(line) : Match.Empty;
            if (match.Success)
            {
                var indent = line.TakeWhile(character => character is ' ' or '\t').Count();
                while (parents.Count > 0 && parents[^1].Indent >= indent)
                {
                    parents.RemoveAt(parents.Count - 1);
                }

                parents.Add((indent, match.Groups["key"].Value));
            }

            if (PlaceholderRegex().IsMatch(line))
            {
                yield return yaml ? string.Join('/', parents.Select(parent => parent.Key)) + "|" + line : line;
            }
        }
    }

    private static List<(string Value, string Placeholder)> Collect(string frontMatter, string prompt)
    {
        var replacements = new List<(string Value, string Placeholder)>();
        foreach (Match match in SecretYamlLineRegex().Matches(frontMatter))
        {
            var key = match.Groups["key"].Value;
            if (!IsSecretKey(key))
            {
                continue;
            }

            var preferred = key.Equals("api_key", StringComparison.OrdinalIgnoreCase) &&
                            !replacements.Any(item => item.Placeholder == WorkflowEditorService.TrackerApiKeyPlaceholder)
                ? WorkflowEditorService.TrackerApiKeyPlaceholder
                : null;
            Add(match.Groups["value"].Value, preferred);
        }

        foreach (Match match in PromptSecretAssignmentRegex().Matches(prompt))
        {
            Add(match.Groups["value"].Value);
        }

        foreach (Match match in GitHubTokenRegex().Matches(prompt))
        {
            Add(match.Value);
        }

        return replacements;

        void Add(string rawValue, string? preferredPlaceholder = null)
        {
            var value = rawValue.Trim().Trim('"', '\'').Trim();
            if (string.IsNullOrWhiteSpace(value) || value is "|" or ">" ||
                value.StartsWith('$') || value.StartsWith("__SYMPHONY_", StringComparison.Ordinal) ||
                replacements.Any(item => item.Value == value))
            {
                return;
            }

            replacements.Add((value, preferredPlaceholder ?? $"__SYMPHONY_SECRET_{replacements.Count + 1}__"));
        }
    }

    private static bool IsSecretKey(string key)
        => key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
           key.Contains("private_key", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[ \t]*(?:-[ \t]*)?(?:(?:""(?<key>[^""]+)"")|'(?<key>[^']+)'|(?<key>[A-Za-z0-9_.-]+))[ \t]*:[ \t]*(?<value>[^\r\n]*?)[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex SecretYamlLineRegex();

    [GeneratedRegex(@"(?i)\b(?:token|password|secret|api[_-]?key)\s*(?:is|[:=])\s*['""`]*(?<value>[A-Za-z0-9_./+=:-]+)")]
    private static partial Regex PromptSecretAssignmentRegex();

    [GeneratedRegex(@"github_pat_[A-Za-z0-9_]+|gh[opusr]_[A-Za-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"__SYMPHONY_(?:KEEP_EXISTING_SECRET|SECRET_[0-9]+)__")]
    private static partial Regex PlaceholderRegex();
}
