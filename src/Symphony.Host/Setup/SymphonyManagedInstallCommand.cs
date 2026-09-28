using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Symphony.Infrastructure.Workflows;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Setup;

internal static partial class SymphonyManagedInstallCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly HashSet<string> ExcludedRootEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", "WORKFLOW.md", "appsettings.json", "data", "workspaces",
        "run-symphony.cmd", "run-symphony.sh"
    };

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        CancellationToken cancellationToken,
        SymphonyInstallationRuntime? runtime = null)
    {
        runtime ??= SymphonyInstallationRuntime.CreateDefault();
        ManagedOptions options;
        try
        {
            options = ManagedOptions.Parse(args);
        }
        catch (ManagedInstallException ex)
        {
            await WriteResultAsync(output, new { status = "blocked", code = ex.Code, message = ex.Message });
            return 2;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            await WriteResultAsync(output, new { status = "blocked", code = "invalid_path",
                message = "A managed install path is invalid." });
            return 2;
        }

        Preflight preflight;
        try
        {
            preflight = await CheckAsync(options, runtime, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SocketException)
        {
            await WriteResultAsync(output, new { status = "blocked", code = "preflight_failed",
                message = "Managed install preflight could not read the source files or inspect the target." });
            return 2;
        }
        if (preflight.Error is not null)
        {
            await WriteResultAsync(output, new
            {
                status = "blocked",
                code = preflight.Error.Code,
                message = preflight.Error.Message,
                instanceId = options.InstanceId,
                instanceDirectory = options.InstanceDirectory,
                port = options.Port
            });
            return 2;
        }

        if (options.PreflightOnly)
        {
            await WriteResultAsync(output, new
            {
                status = "ready",
                code = "preflight_ready",
                instanceId = options.InstanceId,
                instanceDirectory = options.InstanceDirectory,
                port = options.Port,
                secretEnvironmentVariable = options.SecretEnvironmentVariable,
                secretAvailable = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(options.SecretEnvironmentVariable)),
                codexReady = preflight.CodexReady,
                codexIssues = preflight.CodexIssues
            });
            return 0;
        }

        if (options.Launch && !preflight.CodexReady)
        {
            await WriteResultAsync(output, new
            {
                status = "blocked",
                code = "codex_not_ready",
                message = "Codex CLI and authentication must be ready before launch.",
                codexIssues = preflight.CodexIssues
            });
            return 2;
        }

        if (options.Launch && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(options.SecretEnvironmentVariable)))
        {
            await WriteResultAsync(output, new
            {
                status = "blocked",
                code = "secret_not_available",
                message = "The named secret environment variable is unavailable to this process."
            });
            return 2;
        }

        var parent = Path.GetDirectoryName(options.InstanceDirectory)!;
        var staging = Path.Combine(parent, $".{Path.GetFileName(options.InstanceDirectory)}.staging-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(parent);
            Directory.CreateDirectory(staging);
            await CopyBundleAsync(runtime.BundleRootPath, staging, cancellationToken);
            var installedWorkflow = Path.Combine(staging, "WORKFLOW.md");
            await File.WriteAllTextAsync(installedWorkflow,
                await File.ReadAllTextAsync(options.WorkflowPath, cancellationToken), cancellationToken);
            var installedConfig = Path.Combine(staging, "appsettings.json");
            await File.WriteAllTextAsync(installedConfig,
                RenderConfiguration(preflight.Configuration!, options.InstanceId), cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(installedWorkflow, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(installedConfig, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            await File.WriteAllTextAsync(Path.Combine(staging, "run-symphony.cmd"),
                RenderWindowsRunScript(runtime.ExecutableFileName, options.Port), cancellationToken);
            var unixRunScript = Path.Combine(staging, "run-symphony.sh");
            await File.WriteAllTextAsync(unixRunScript,
                RenderUnixRunScript(runtime.ExecutableFileName, options.Port), cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(unixRunScript,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, options.InstanceDirectory);

            int? processId = null;
            var launchFailed = false;
            if (options.Launch)
            {
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = Path.Combine(options.InstanceDirectory, runtime.ExecutableFileName),
                        WorkingDirectory = options.InstanceDirectory,
                        UseShellExecute = false
                    };
                    startInfo.ArgumentList.Add("WORKFLOW.md");
                    startInfo.ArgumentList.Add("--port");
                    startInfo.ArgumentList.Add(options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    using var process = Process.Start(startInfo);
                    processId = process?.Id;
                    launchFailed = process is null;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or
                                           UnauthorizedAccessException or InvalidOperationException)
                {
                    launchFailed = true;
                }
            }

            await WriteResultAsync(output, new
            {
                status = "installed",
                code = launchFailed ? "launch_failed" : "install_complete",
                instanceId = options.InstanceId,
                instanceDirectory = options.InstanceDirectory,
                workflowPath = Path.Combine(options.InstanceDirectory, "WORKFLOW.md"),
                configPath = Path.Combine(options.InstanceDirectory, "appsettings.json"),
                port = options.Port,
                url = $"http://127.0.0.1:{options.Port}/",
                secretEnvironmentVariable = options.SecretEnvironmentVariable,
                secretAvailable = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(options.SecretEnvironmentVariable)),
                codexReady = preflight.CodexReady,
                codexIssues = preflight.CodexIssues,
                started = processId.HasValue,
                processId
            });
            return launchFailed ? 1 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ManagedInstallException)
        {
            var code = ex is ManagedInstallException managed ? managed.Code : "install_failed";
            await WriteResultAsync(output, new { status = "failed", code,
                message = "Managed installation failed. Check target directory permissions and collisions." });
            return 1;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task<Preflight> CheckAsync(
        ManagedOptions options,
        SymphonyInstallationRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(options.WorkflowPath) || !File.Exists(options.ConfigPath))
        {
            return new Preflight(new ManagedInstallException("source_missing", "Workflow and config source files must exist."));
        }

        var bundleRoot = Path.GetFullPath(runtime.BundleRootPath);
        if (Directory.Exists(options.InstanceDirectory) ||
            IsWithin(bundleRoot, options.InstanceDirectory))
        {
            return new Preflight(new ManagedInstallException("directory_collision", "The instance directory already exists or is inside the package bundle."));
        }

        if (!File.Exists(Path.Combine(bundleRoot, runtime.ExecutableFileName)))
        {
            return new Preflight(new ManagedInstallException("bundle_missing", "The package executable is missing."));
        }

        using (var listener = new TcpListener(IPAddress.Loopback, options.Port))
        {
            try { listener.Start(); }
            catch (SocketException)
            {
                return new Preflight(new ManagedInstallException("port_collision", "The loopback port is unavailable."));
            }
        }

        JsonObject configuration;
        try
        {
            var configText = await File.ReadAllTextAsync(options.ConfigPath, cancellationToken);
            configuration = JsonNode.Parse(configText) as JsonObject
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return new Preflight(new ManagedInstallException("invalid_config", "Config source must be a JSON object."));
        }

        try
        {
            var definition = await new WorkflowLoader().LoadAsync(options.WorkflowPath, cancellationToken);
            if (!string.Equals(definition.Runtime.Tracker.ApiKey,
                "$" + options.SecretEnvironmentVariable, StringComparison.Ordinal))
            {
                return new Preflight(new ManagedInstallException("invalid_secret_reference",
                    "tracker.api_key must reference the named environment variable."));
            }
        }
        catch (WorkflowLoadException ex)
        {
            return new Preflight(new ManagedInstallException(ex.Code, "Workflow source failed validation."));
        }

        var codex = await runtime.CodexCliPreflightAsync(cancellationToken);
        return new Preflight(null, configuration, codex.IsReadyToStart,
            codex.BlockingIssues.Select(_ => "Codex CLI or authentication prerequisite is not ready.").Distinct().ToArray());
    }

    private static async Task CopyBundleAsync(string sourceRoot, string destinationRoot, CancellationToken cancellationToken)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceRoot, sourceFile);
            var first = relative.Replace('\\', '/').Split('/', 2)[0];
            if (ExcludedRootEntries.Contains(first)) { continue; }
            var destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourceFile, destination, overwrite: false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(destination, File.GetUnixFileMode(sourceFile));
            }
        }
    }

    private static string RenderConfiguration(JsonObject configuration, string instanceId)
    {
        var result = (JsonObject)configuration.DeepClone();
        SetSection(result, "Orchestration", "InstanceId", instanceId);
        SetSection(result, "Workflow", "Path", "WORKFLOW.md");
        SetSection(result, "Persistence", "ConnectionString",
            "Data Source=./data/symphony.db;Cache=Shared;Mode=ReadWriteCreate");
        return result.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void SetSection(JsonObject root, string sectionName, string key, string value)
    {
        var actualSectionName = root.Select(item => item.Key)
            .FirstOrDefault(name => name.Equals(sectionName, StringComparison.OrdinalIgnoreCase)) ?? sectionName;
        if (root[actualSectionName] is not JsonObject section)
        {
            section = new JsonObject();
            root[actualSectionName] = section;
        }
        var actualKey = section.Select(item => item.Key)
            .FirstOrDefault(name => name.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
        section[actualKey] = value;
    }

    private static string RenderWindowsRunScript(string executableName, int port)
        => $"@echo off\r\nsetlocal\r\npushd \"%~dp0\" >nul\r\n\"%~dp0{executableName}\" WORKFLOW.md --port {port} %*\r\nset \"exitCode=%ERRORLEVEL%\"\r\npopd >nul\r\nexit /b %exitCode%\r\n";

    private static string RenderUnixRunScript(string executableName, int port)
        => $"#!/usr/bin/env bash\nset -euo pipefail\ncd \"$(dirname \"${{BASH_SOURCE[0]}}\")\"\nexec ./\"{executableName}\" WORKFLOW.md --port {port} \"$@\"\n";

    private static bool IsWithin(string parent, string candidate)
    {
        var prefix = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(prefix,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static Task WriteResultAsync(TextWriter output, object result)
        => output.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));

    private sealed record Preflight(
        ManagedInstallException? Error,
        JsonObject? Configuration = null,
        bool CodexReady = false,
        IReadOnlyList<string>? CodexIssues = null);

    private sealed class ManagedInstallException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private sealed record ManagedOptions(
        string InstanceId,
        string InstanceDirectory,
        string WorkflowPath,
        string ConfigPath,
        int Port,
        string SecretEnvironmentVariable,
        bool PreflightOnly,
        bool Launch)
    {
        public static ManagedOptions Parse(IReadOnlyList<string> args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var preflightOnly = false;
            var launch = false;
            for (var index = 0; index < args.Count; index++)
            {
                var option = args[index].ToLowerInvariant();
                if (option.Equals("--preflight-only", StringComparison.OrdinalIgnoreCase)) { preflightOnly = true; continue; }
                if (option.Equals("--launch", StringComparison.OrdinalIgnoreCase)) { launch = true; continue; }
                if (option is not ("--instance-id" or "--instance-dir" or "--workflow-path" or
                    "--config-path" or "--port" or "--github-token-env") || index + 1 >= args.Count)
                {
                    throw new ManagedInstallException("invalid_option", "Managed install options are incomplete or unsupported.");
                }
                if (!values.TryAdd(option, args[++index]))
                {
                    throw new ManagedInstallException("duplicate_option", "Managed install options must appear once.");
                }
            }

            string Required(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value : throw new ManagedInstallException("missing_option", $"Required managed install option {key} is missing.");

            var id = Required("--instance-id");
            var secretEnv = Required("--github-token-env");
            if (!InstanceIdRegex().IsMatch(id))
            {
                throw new ManagedInstallException("invalid_instance_id", "Instance ID must use letters, digits, dots, underscores, or hyphens.");
            }
            if (!EnvironmentNameRegex().IsMatch(secretEnv))
            {
                throw new ManagedInstallException("invalid_secret_reference", "Secret reference must be an environment variable name.");
            }
            if (!int.TryParse(Required("--port"), out var port) || port < 1 || port > 65535)
            {
                throw new ManagedInstallException("invalid_port", "Port must be between 1 and 65535.");
            }
            if (launch && preflightOnly)
            {
                throw new ManagedInstallException("invalid_option", "Preflight-only and launch cannot be combined.");
            }

            return new ManagedOptions(id, Path.GetFullPath(Required("--instance-dir")),
                Path.GetFullPath(Required("--workflow-path")), Path.GetFullPath(Required("--config-path")),
                port, secretEnv, preflightOnly, launch);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex InstanceIdRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvironmentNameRegex();
}
