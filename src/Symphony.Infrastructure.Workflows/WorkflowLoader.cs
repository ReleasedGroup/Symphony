using Symphony.Core.Defaults;
using Symphony.Infrastructure.Workflows.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Core;

namespace Symphony.Infrastructure.Workflows;

public sealed class WorkflowLoader
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder().Build();

    public async Task<WorkflowDefinition> LoadAsync(string workflowPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(workflowPath))
        {
            throw new WorkflowLoadException("missing_workflow_file", $"Workflow file was not found at '{workflowPath}'.");
        }

        string rawContent;
        try
        {
            rawContent = await File.ReadAllTextAsync(workflowPath, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new WorkflowLoadException("missing_workflow_file", $"Workflow file could not be read at '{workflowPath}'.", ex);
        }

        var (config, promptTemplate) = ParseWorkflowContent(rawContent);
        var runtime = ParseRuntimeSettings(config);

        return new WorkflowDefinition(
            config,
            promptTemplate,
            runtime,
            workflowPath,
            DateTimeOffset.UtcNow);
    }

    private static (IReadOnlyDictionary<string, object?> Config, string PromptTemplate) ParseWorkflowContent(string rawContent)
    {
        var normalized = rawContent.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), normalized.Trim());
        }

        var lines = normalized.Split('\n');
        var closingIndex = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Equals("---", StringComparison.Ordinal))
            {
                closingIndex = i;
                break;
            }
        }

        if (closingIndex < 0)
        {
            throw new WorkflowLoadException("workflow_parse_error", "Workflow front matter starts with '---' but has no closing '---'.");
        }

        var yamlText = string.Join('\n', lines[1..closingIndex]);
        var markdownBody = closingIndex + 1 >= lines.Length ? string.Empty : string.Join('\n', lines[(closingIndex + 1)..]).Trim();

        if (string.IsNullOrWhiteSpace(yamlText))
        {
            return (new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), markdownBody);
        }

        try
        {
            var yamlRoot = YamlDeserializer.Deserialize<object>(yamlText);
            var config = ConvertToRootMap(yamlRoot);
            return (config, markdownBody);
        }
        catch (YamlException ex)
        {
            throw new WorkflowLoadException("workflow_parse_error", $"Workflow front matter YAML could not be parsed: {ex.Message}", ex);
        }
    }

    private static IReadOnlyDictionary<string, object?> ConvertToRootMap(object? yamlRoot)
    {
        if (yamlRoot is null)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        if (yamlRoot is not IDictionary<object, object?> map)
        {
            throw new WorkflowLoadException("workflow_front_matter_not_a_map", "Workflow front matter must decode to a map/object.");
        }

        return ConvertMap(map);
    }

    private static Dictionary<string, object?> ConvertMap(IDictionary<object, object?> map)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (keyObj, valueObj) in map)
        {
            var key = keyObj?.ToString();
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new WorkflowLoadException("workflow_parse_error", "Workflow front matter map keys must be non-empty strings.");
            }

            result[key] = ConvertValue(valueObj);
        }

        return result;
    }

    private static object? ConvertValue(object? value)
    {
        return value switch
        {
            IDictionary<object, object?> map => ConvertMap(map),
            IList<object?> list => list.Select(ConvertValue).ToList(),
            System.Collections.IList list => list.Cast<object?>().Select(ConvertValue).ToList(),
            _ => value
        };
    }

    private static WorkflowRuntimeSettings ParseRuntimeSettings(IReadOnlyDictionary<string, object?> config)
    {
        var trackerMap = GetOptionalMap(config, "tracker")
            ?? throw new WorkflowLoadException("missing_tracker_config", "Workflow front matter must include 'tracker'.");

        var kind = GetRequiredString(trackerMap, "kind", "missing_tracker_kind");
        if (!kind.Equals("github", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkflowLoadException("unsupported_tracker_kind", $"Unsupported tracker.kind '{kind}'. Expected 'github'.");
        }

        var endpoint = GetOptionalString(trackerMap, "endpoint") ?? "https://api.github.com/graphql";
        var apiKey = GetRequiredString(trackerMap, "api_key", "missing_tracker_api_key");
        var owner = GetRequiredString(trackerMap, "owner", "missing_tracker_owner");
        var repo = GetRequiredString(trackerMap, "repo", "missing_tracker_repo");
        var milestone = GetOptionalStringOrNumber(trackerMap, "milestone");
        var labels = GetStringList(trackerMap, "labels", []);
        var activeStates = GetStringList(trackerMap, "active_states", ["Open", "In Progress"]);
        var terminalStates = GetStringList(trackerMap, "terminal_states", ["Closed"]);

        var pollingMap = GetOptionalMap(config, "polling");
        var intervalMs = GetOptionalInt(pollingMap, "interval_ms", SymphonyDefaults.PollIntervalMs);
        if (intervalMs < 1_000)
        {
            throw new WorkflowLoadException("invalid_polling_interval", "polling.interval_ms must be >= 1000.");
        }

        var agentMap = GetOptionalMap(config, "agent");
        var maxConcurrentAgents = GetOptionalInt(agentMap, "max_concurrent_agents", SymphonyDefaults.MaxConcurrentAgents);
        if (maxConcurrentAgents <= 0)
        {
            throw new WorkflowLoadException("invalid_max_concurrent_agents", "agent.max_concurrent_agents must be > 0.");
        }

        return new WorkflowRuntimeSettings(
            new WorkflowTrackerSettings(
                kind.ToLowerInvariant(),
                endpoint,
                apiKey,
                owner,
                repo,
                milestone,
                labels,
                activeStates,
                terminalStates),
            new WorkflowPollingSettings(intervalMs),
            new WorkflowAgentSettings(maxConcurrentAgents));
    }

    private static Dictionary<string, object?>? GetOptionalMap(IReadOnlyDictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        if (raw is not Dictionary<string, object?> map)
        {
            throw new WorkflowLoadException("workflow_parse_error", $"'{key}' must be an object/map.");
        }

        return map;
    }

    private static string GetRequiredString(Dictionary<string, object?> source, string key, string errorCode)
    {
        var value = GetOptionalString(source, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new WorkflowLoadException(errorCode, $"'{key}' is required.");
        }

        return value;
    }

    private static string? GetOptionalString(Dictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            string str => str.Trim(),
            _ => throw new WorkflowLoadException("workflow_parse_error", $"'{key}' must be a string.")
        };
    }

    private static string? GetOptionalStringOrNumber(Dictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            string str => str.Trim(),
            int intValue => intValue.ToString(),
            long longValue => longValue.ToString(),
            _ => throw new WorkflowLoadException("workflow_parse_error", $"'{key}' must be a string or integer.")
        };
    }

    private static List<string> GetStringList(Dictionary<string, object?> source, string key, IReadOnlyList<string> defaultValues)
    {
        if (!source.TryGetValue(key, out var raw) || raw is null)
        {
            return defaultValues.ToList();
        }

        switch (raw)
        {
            case string csv:
                return csv
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
            case IList<object?> list:
                return list.Select(item => item?.ToString()?.Trim())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Cast<string>()
                    .ToList();
            case System.Collections.IList list:
                return list.Cast<object?>()
                    .Select(item => item?.ToString()?.Trim())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Cast<string>()
                    .ToList();
            default:
                throw new WorkflowLoadException("workflow_parse_error", $"'{key}' must be a list of strings or comma-separated string.");
        }
    }

    private static int GetOptionalInt(Dictionary<string, object?>? source, string key, int defaultValue)
    {
        if (source is null || !source.TryGetValue(key, out var raw) || raw is null)
        {
            return defaultValue;
        }

        return raw switch
        {
            int intValue => intValue,
            long longValue when longValue <= int.MaxValue && longValue >= int.MinValue => (int)longValue,
            string str when int.TryParse(str, out var parsed) => parsed,
            _ => throw new WorkflowLoadException("workflow_parse_error", $"'{key}' must be an integer.")
        };
    }
}
