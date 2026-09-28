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

    private static List<(string Value, string Placeholder)> Collect(string frontMatter, string prompt)
    {
        var replacements = new List<(string Value, string Placeholder)>();
        var trackerMatch = TrackerApiKeyRegex().Match(frontMatter);
        if (trackerMatch.Success)
        {
            Add(trackerMatch.Groups["value"].Value, WorkflowEditorService.TrackerApiKeyPlaceholder);
        }

        foreach (Match match in SecretYamlLineRegex().Matches(frontMatter))
        {
            if (!IsSecretKey(match.Groups["key"].Value))
            {
                continue;
            }

            Add(match.Groups["value"].Value);
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

    [GeneratedRegex(@"^\s*api_key\s*:\s*(?<value>.*?)\s*$", RegexOptions.Multiline)]
    private static partial Regex TrackerApiKeyRegex();

    [GeneratedRegex(@"^\s*(?:-\s*)?(?<key>[A-Za-z0-9_.-]+)\s*:\s*(?<value>[^\r\n]*?)\s*$", RegexOptions.Multiline)]
    private static partial Regex SecretYamlLineRegex();

    [GeneratedRegex(@"(?i)\b(?:token|password|secret|api[_-]?key)\s*(?:is|[:=])\s*['""`]*(?<value>[A-Za-z0-9_./+=:-]{4,})")]
    private static partial Regex PromptSecretAssignmentRegex();

    [GeneratedRegex(@"github_pat_[A-Za-z0-9_]+|gh[opusr]_[A-Za-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"__SYMPHONY_(?:KEEP_EXISTING_SECRET|SECRET_[0-9]+)__")]
    private static partial Regex PlaceholderRegex();
}
