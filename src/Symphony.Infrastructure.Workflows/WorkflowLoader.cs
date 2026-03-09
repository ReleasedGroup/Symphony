using Symphony.Core.Defaults;
using Symphony.Infrastructure.Workflows.Models;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

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
        var includePullRequests = GetOptionalBoolean(trackerMap, "include_pull_requests", defaultValue: true);
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

        var maxTurns = GetOptionalInt(agentMap, "max_turns", 20);
        if (maxTurns <= 0)
        {
            throw new WorkflowLoadException("invalid_agent_max_turns", "agent.max_turns must be > 0.");
        }

        var maxRetryBackoffMs = GetOptionalInt(agentMap, "max_retry_backoff_ms", 300_000);
        if (maxRetryBackoffMs <= 0)
        {
            throw new WorkflowLoadException("invalid_agent_max_retry_backoff", "agent.max_retry_backoff_ms must be > 0.");
        }

        var maxConcurrentAgentsByState = GetNormalizedPositiveIntMap(agentMap, "max_concurrent_agents_by_state");

        var workspaceMap = GetOptionalMap(config, "workspace");
        var workspaceRoot = GetResolvedPathLikeValue(workspaceMap, "root", "./workspaces");
        var sharedClonePath = GetResolvedPathLikeValue(workspaceMap, "shared_clone_path", "./workspaces/repo");
        var worktreesRoot = GetResolvedPathLikeValue(workspaceMap, "worktrees_root", "./workspaces/worktrees");
        var baseBranch = GetOptionalStringFromOptionalMap(workspaceMap, "base_branch") ?? "main";
        var remoteUrl = GetOptionalStringFromOptionalMap(workspaceMap, "remote_url");

        if (string.IsNullOrWhiteSpace(baseBranch))
        {
            throw new WorkflowLoadException("invalid_workspace_base_branch", "workspace.base_branch must be non-empty.");
        }

        var hooksMap = GetOptionalMap(config, "hooks");
        var hooksAfterCreate = GetOptionalScriptFromOptionalMap(hooksMap, "after_create");
        var hooksBeforeRun = GetOptionalScriptFromOptionalMap(hooksMap, "before_run");
        var hooksAfterRun = GetOptionalScriptFromOptionalMap(hooksMap, "after_run");
        var hooksBeforeRemove = GetOptionalScriptFromOptionalMap(hooksMap, "before_remove");
        var hooksTimeoutMs = GetOptionalInt(hooksMap, "timeout_ms", 60_000);
        if (hooksTimeoutMs <= 0)
        {
            hooksTimeoutMs = 60_000;
        }

        var codexMap = GetOptionalMap(config, "codex");
        var codexCommand = GetOptionalStringFromOptionalMap(codexMap, "command") ?? "codex app-server";
        if (string.IsNullOrWhiteSpace(codexCommand))
        {
            throw new WorkflowLoadException("invalid_codex_command", "codex.command must be non-empty.");
        }

        var codexTurnTimeoutMs = GetOptionalInt(
            codexMap,
            "turn_timeout_ms",
            GetOptionalInt(codexMap, "timeout_ms", 3_600_000));
        if (codexTurnTimeoutMs <= 0)
        {
            throw new WorkflowLoadException("invalid_codex_turn_timeout", "codex.turn_timeout_ms must be > 0.");
        }

        var codexApprovalPolicy = GetOptionalStringFromOptionalMap(codexMap, "approval_policy") ?? "never";
        if (string.IsNullOrWhiteSpace(codexApprovalPolicy))
        {
            throw new WorkflowLoadException("invalid_codex_approval_policy", "codex.approval_policy must be non-empty.");
        }

        var codexThreadSandbox = GetOptionalStringFromOptionalMap(codexMap, "thread_sandbox") ?? "danger-full-access";
        if (string.IsNullOrWhiteSpace(codexThreadSandbox))
        {
            throw new WorkflowLoadException("invalid_codex_thread_sandbox", "codex.thread_sandbox must be non-empty.");
        }

        var codexTurnSandboxPolicy = GetOptionalStringFromOptionalMap(codexMap, "turn_sandbox_policy") ?? "danger-full-access";
        if (string.IsNullOrWhiteSpace(codexTurnSandboxPolicy))
        {
            throw new WorkflowLoadException("invalid_codex_turn_sandbox_policy", "codex.turn_sandbox_policy must be non-empty.");
        }

        var codexReadTimeoutMs = GetOptionalInt(codexMap, "read_timeout_ms", 5_000);
        if (codexReadTimeoutMs <= 0)
        {
            throw new WorkflowLoadException("invalid_codex_read_timeout", "codex.read_timeout_ms must be > 0.");
        }

        var codexStallTimeoutMs = GetOptionalInt(codexMap, "stall_timeout_ms", 300_000);

        return new WorkflowRuntimeSettings(
            new WorkflowTrackerSettings(
                kind.ToLowerInvariant(),
                endpoint,
                apiKey,
                owner,
                repo,
                milestone,
                includePullRequests,
                labels,
                activeStates,
                terminalStates),
            new WorkflowPollingSettings(intervalMs),
            new WorkflowAgentSettings(
                maxConcurrentAgents,
                maxTurns,
                maxRetryBackoffMs,
                maxConcurrentAgentsByState),
            new WorkflowWorkspaceSettings(
                workspaceRoot,
                sharedClonePath,
                worktreesRoot,
                baseBranch,
                remoteUrl),
            new WorkflowHooksSettings(
                hooksAfterCreate,
                hooksBeforeRun,
                hooksAfterRun,
                hooksBeforeRemove,
                hooksTimeoutMs),
            new WorkflowCodexSettings(
                codexCommand,
                codexTurnTimeoutMs,
                codexApprovalPolicy,
                codexThreadSandbox,
                codexTurnSandboxPolicy,
                codexReadTimeoutMs,
                codexStallTimeoutMs));
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

    private static string? GetOptionalStringFromOptionalMap(Dictionary<string, object?>? source, string key)
    {
        if (source is null)
        {
            return null;
        }

        return GetOptionalString(source, key);
    }

    private static string? GetOptionalScriptFromOptionalMap(Dictionary<string, object?>? source, string key)
    {
        if (source is null || !source.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        if (raw is not string script)
        {
            throw new WorkflowLoadException("workflow_parse_error", $"'{key}' must be a string.");
        }

        return string.IsNullOrWhiteSpace(script) ? null : script;
    }

    private static string GetResolvedPathLikeValue(Dictionary<string, object?>? source, string key, string defaultValue)
    {
        var configuredValue = GetOptionalStringFromOptionalMap(source, key);
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return defaultValue;
        }

        var resolvedValue = WorkflowValueResolver.ResolvePathLikeValue(configuredValue);
        return string.IsNullOrWhiteSpace(resolvedValue) ? defaultValue : resolvedValue;
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

    private static bool GetOptionalBoolean(Dictionary<string, object?> source, string key, bool defaultValue)
    {
        if (!source.TryGetValue(key, out var raw) || raw is null)
        {
            return defaultValue;
        }

        return raw switch
        {
            bool boolValue => boolValue,
            string str when bool.TryParse(str, out var parsed) => parsed,
            _ => throw new WorkflowLoadException("workflow_parse_error", $"'{key}' must be a boolean.")
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

    private static Dictionary<string, int> GetNormalizedPositiveIntMap(Dictionary<string, object?>? source, string key)
    {
        if (source is null || !source.TryGetValue(key, out var raw) || raw is null)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        if (raw is not Dictionary<string, object?> map)
        {
            throw new WorkflowLoadException("workflow_parse_error", $"'{key}' must be an object/map.");
        }

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawState, rawValue) in map)
        {
            var normalizedState = NormalizeStateKey(rawState);
            if (string.IsNullOrWhiteSpace(normalizedState) || !TryGetInt(rawValue, out var parsedValue) || parsedValue <= 0)
            {
                continue;
            }

            result[normalizedState] = parsedValue;
        }

        return result;
    }

    private static int GetOptionalInt(Dictionary<string, object?>? source, string key, int defaultValue)
    {
        if (source is null || !source.TryGetValue(key, out var raw) || raw is null)
        {
            return defaultValue;
        }

        if (!TryGetInt(raw, out var parsed))
        {
            throw new WorkflowLoadException("workflow_parse_error", $"'{key}' must be an integer.");
        }

        return parsed;
    }

    private static bool TryGetInt(object? raw, out int value)
    {
        switch (raw)
        {
            case int intValue:
                value = intValue;
                return true;
            case long longValue when longValue <= int.MaxValue && longValue >= int.MinValue:
                value = (int)longValue;
                return true;
            case string str when int.TryParse(str, out var parsed):
                value = parsed;
                return true;
            default:
                value = default;
                return false;
        }
    }

    private static string NormalizeStateKey(string rawState)
    {
        return rawState.Trim().ToLowerInvariant();
    }
}
