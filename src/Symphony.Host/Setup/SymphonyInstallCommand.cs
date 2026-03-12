using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Symphony.Core.Defaults;
using Symphony.Core.Metadata;

namespace Symphony.Host.Setup;

internal static class SymphonyInstallCommand
{
    private const string EnvironmentFileName = ".env";
    private const string WorkflowFileName = "WORKFLOW.md";
    private const string WindowsRunScriptFileName = "run-symphony.cmd";
    private const string UnixRunScriptFileName = "run-symphony.sh";
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly HashSet<string> ExcludedRootEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        EnvironmentFileName,
        WorkflowFileName,
        "appsettings.json",
        "data",
        "workspaces",
        WindowsRunScriptFileName,
        UnixRunScriptFileName
    };

    public static async Task<int> RunAsync(
        InstallCommandOptions options,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        SymphonyInstallationRuntime? runtime = null)
    {
        runtime ??= SymphonyInstallationRuntime.CreateDefault();

        if (options.ShowHelp)
        {
            await output.WriteLineAsync("""
                Usage: Symphony install [--no-launch]

                Interactive installer for a self-contained Symphony instance.
                The installer creates a dedicated WORKFLOW.md, appsettings.json,
                .env, and run scripts in the selected folder. Before launch,
                it also checks the local Codex CLI version and auth state.
                """);
            return 0;
        }

        var bundleRoot = Path.GetFullPath(runtime.BundleRootPath);
        var suggestedPort = GetSuggestedPort();

        await output.WriteLineAsync($"{SymphonyProductInfo.Name} {SymphonyProductInfo.DisplayVersion}");
        await output.WriteLineAsync("Interactive setup creates an isolated instance with its own URL, database, and workspaces.");
        await output.WriteLineAsync("Press Enter to accept a default value when one is shown in brackets.");
        await output.WriteLineAsync(string.Empty);

        var token = await PromptRequiredSecretAsync(input, output, cancellationToken);
        var owner = await PromptRequiredAsync(input, output, "GitHub owner", defaultValue: null, cancellationToken);
        var repo = await PromptRequiredAsync(input, output, "GitHub repository", defaultValue: null, cancellationToken);
        var baseBranch = await PromptRequiredAsync(input, output, "Base branch", "main", cancellationToken);

        var defaultInstallFolder = bundleRoot;
        var installFolder = await PromptRequiredAsync(input, output, "Instance folder", defaultInstallFolder, cancellationToken);
        installFolder = Path.GetFullPath(installFolder);

        var portText = await PromptRequiredAsync(input, output, "HTTP port", suggestedPort.ToString(), cancellationToken);
        if (!int.TryParse(portText, out var port) || port <= 0 || port > 65535)
        {
            throw new SymphonyCliException($"The HTTP port must be an integer between 1 and 65535. Received '{portText}'.");
        }

        if (IsNestedUnder(bundleRoot, installFolder) && !PathsEqual(bundleRoot, installFolder))
        {
            throw new SymphonyCliException("The instance folder cannot be nested inside the current package bundle. Choose the bundle folder itself or a separate sibling path.");
        }

        if (!PathsEqual(bundleRoot, installFolder) &&
            Directory.Exists(installFolder) &&
            Directory.EnumerateFileSystemEntries(installFolder).Any())
        {
            var continueWithOverwrite = await PromptConfirmationAsync(
                input,
                output,
                $"'{installFolder}' already contains files. Continue and overwrite matching bundle/config files?",
                defaultValue: false,
                cancellationToken);

            if (!continueWithOverwrite)
            {
                await output.WriteLineAsync("Installation canceled.");
                return 1;
            }
        }

        var instanceId = BuildInstanceId(repo, installFolder);
        var remoteUrl = $"https://github.com/{owner}/{repo}.git";

        Directory.CreateDirectory(installFolder);
        await CopyBundleAsync(bundleRoot, installFolder, cancellationToken);

        var environmentFilePath = Path.Combine(installFolder, EnvironmentFileName);
        await File.WriteAllTextAsync(
            environmentFilePath,
            RenderEnvironmentFile(token),
            Utf8WithoutBom,
            cancellationToken);
        EnsureUnixSecretMode(environmentFilePath);

        await File.WriteAllTextAsync(
            Path.Combine(installFolder, "appsettings.json"),
            RenderAppSettingsJson(instanceId),
            Utf8WithoutBom,
            cancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(installFolder, WorkflowFileName),
            RenderWorkflow(owner, repo, baseBranch, remoteUrl, port),
            Utf8WithoutBom,
            cancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(installFolder, WindowsRunScriptFileName),
            RenderWindowsRunScript(runtime.ExecutableFileName),
            Utf8WithoutBom,
            cancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(installFolder, UnixRunScriptFileName),
            RenderUnixRunScript(runtime.ExecutableFileName),
            Utf8WithoutBom,
            cancellationToken);

        EnsureUnixModes(
            Path.Combine(installFolder, runtime.ExecutableFileName),
            Path.Combine(installFolder, runtime.UnixSetupScriptFileName),
            Path.Combine(installFolder, UnixRunScriptFileName));

        var url = $"http://127.0.0.1:{port}/";
        await output.WriteLineAsync(string.Empty);
        await output.WriteLineAsync($"Installed {SymphonyProductInfo.Name} into '{installFolder}'.");
        await output.WriteLineAsync($"Instance ID: {instanceId}");
        await output.WriteLineAsync($"Instance URL: {url}");
        await output.WriteLineAsync($"Start again later with '{GetPreferredRunCommand()}' from the instance folder.");

        var codexReady = await EnsureCodexReadyAsync(
            options,
            input,
            output,
            runtime,
            cancellationToken);

        if (options.NoLaunch)
        {
            return 0;
        }

        if (!codexReady)
        {
            return 1;
        }

        var shouldLaunch = await PromptConfirmationAsync(
            input,
            output,
            "Start Symphony now?",
            defaultValue: true,
            cancellationToken);

        if (!shouldLaunch)
        {
            return 0;
        }

        var executablePath = Path.Combine(installFolder, runtime.ExecutableFileName);
        if (!File.Exists(executablePath))
        {
            throw new SymphonyCliException(
                $"The packaged executable '{runtime.ExecutableFileName}' was not found in '{installFolder}'.");
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = installFolder,
                UseShellExecute = false
            }
        };

        process.StartInfo.Environment["GITHUB_TOKEN"] = token;

        await output.WriteLineAsync($"Launching Symphony at {url}");
        if (!process.Start())
        {
            throw new SymphonyCliException("Failed to start Symphony after installation.");
        }

        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private static async Task<bool> EnsureCodexReadyAsync(
        InstallCommandOptions options,
        TextReader input,
        TextWriter output,
        SymphonyInstallationRuntime runtime,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var preflight = await runtime.CodexCliPreflightAsync(cancellationToken);
            await WriteCodexPreflightAsync(output, preflight, cancellationToken);

            if (preflight.IsReadyToStart)
            {
                return true;
            }

            await output.WriteLineAsync("Fix the Codex CLI items above before Symphony starts.");

            if (options.NoLaunch)
            {
                await output.WriteLineAsync($"Before your first run, fix the Codex CLI items above and then start Symphony with '{GetPreferredRunCommand()}'.");
                return false;
            }

            var retry = await PromptConfirmationAsync(
                input,
                output,
                "Re-check Codex CLI after you update/sign in?",
                defaultValue: true,
                cancellationToken);

            if (!retry)
            {
                await output.WriteLineAsync("Installation completed, but Symphony was not started.");
                return false;
            }

            await output.WriteLineAsync("Re-checking Codex CLI...");
        }
    }

    private static async Task CopyBundleAsync(string sourceRoot, string destinationRoot, CancellationToken cancellationToken)
    {
        if (PathsEqual(sourceRoot, destinationRoot))
        {
            return;
        }

        foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            if (ShouldExclude(relativePath))
            {
                continue;
            }

            var destinationFile = Path.Combine(destinationRoot, relativePath);
            var destinationDirectory = Path.GetDirectoryName(destinationFile);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Copy(sourceFile, destinationFile, overwrite: true);
            TryCopyUnixMode(sourceFile, destinationFile);
        }
    }

    private static async Task WriteCodexPreflightAsync(
        TextWriter output,
        CodexCliPreflightResult preflight,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await output.WriteLineAsync(string.Empty);
        await output.WriteLineAsync("Codex CLI check:");
        await output.WriteLineAsync($"- Installed version: {preflight.InstalledVersion ?? "not found"}");
        await output.WriteLineAsync($"- Symphony validated version: {preflight.ValidatedVersion}");
        await output.WriteLineAsync($"- Latest available version: {FormatLatestVersion(preflight)}");
        await output.WriteLineAsync($"- Auth file: {preflight.AuthJsonPath}");
        await output.WriteLineAsync($"- Auth file present: {(preflight.HasAuthJson ? "yes" : "no")}");
        await output.WriteLineAsync($"- Login status: {(preflight.LoginConfigured ? "ready" : "not ready")}");

        if (preflight.Warnings.Count > 0)
        {
            await output.WriteLineAsync("Notes:");
            foreach (var warning in preflight.Warnings)
            {
                await output.WriteLineAsync($"- {warning}");
            }
        }

        if (preflight.IsReadyToStart)
        {
            await output.WriteLineAsync("Codex CLI is ready.");
            return;
        }

        await output.WriteLineAsync("Before starting Symphony, fix these Codex CLI items:");
        foreach (var issue in preflight.BlockingIssues)
        {
            await output.WriteLineAsync($"- {issue}");
        }

        if (preflight.RemediationSteps.Count > 0)
        {
            await output.WriteLineAsync("Suggested next steps:");
            foreach (var step in preflight.RemediationSteps)
            {
                await output.WriteLineAsync($"- {step}");
            }
        }
    }

    private static string FormatLatestVersion(CodexCliPreflightResult preflight)
    {
        if (string.IsNullOrWhiteSpace(preflight.LatestVersion))
        {
            return "unavailable";
        }

        if (string.IsNullOrWhiteSpace(preflight.LatestVersionSource))
        {
            return preflight.LatestVersion;
        }

        return $"{preflight.LatestVersion} ({preflight.LatestVersionSource})";
    }

    private static bool ShouldExclude(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var firstSegment = normalized.Split('/', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return ExcludedRootEntries.Contains(firstSegment);
    }

    private static async Task<string> PromptRequiredSecretAsync(
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteAsync("GitHub token: ");

            string? token;
            if (ReferenceEquals(input, Console.In) &&
                ReferenceEquals(output, Console.Out) &&
                !Console.IsInputRedirected &&
                !Console.IsOutputRedirected)
            {
                token = ReadSecretFromConsole();
                await output.WriteLineAsync();
            }
            else
            {
                token = await input.ReadLineAsync(cancellationToken);
                if (token is null)
                {
                    throw new SymphonyCliException("GitHub token input ended unexpectedly.");
                }
            }

            if (!string.IsNullOrWhiteSpace(token))
            {
                return token.Trim();
            }

            await output.WriteLineAsync("A GitHub token is required.");
        }
    }

    private static async Task<string> PromptRequiredAsync(
        TextReader input,
        TextWriter output,
        string label,
        string? defaultValue,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(defaultValue))
            {
                await output.WriteAsync($"{label}: ");
            }
            else
            {
                await output.WriteAsync($"{label} [{defaultValue}]: ");
            }

            var response = await input.ReadLineAsync(cancellationToken);
            if (response is null)
            {
                throw new SymphonyCliException($"{label} input ended unexpectedly.");
            }

            if (!string.IsNullOrWhiteSpace(response))
            {
                return response.Trim();
            }

            if (!string.IsNullOrWhiteSpace(defaultValue))
            {
                return defaultValue;
            }

            await output.WriteLineAsync($"{label} is required.");
        }
    }

    private static async Task<bool> PromptConfirmationAsync(
        TextReader input,
        TextWriter output,
        string label,
        bool defaultValue,
        CancellationToken cancellationToken)
    {
        var suffix = defaultValue ? " [Y/n]: " : " [y/N]: ";

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteAsync(label);
            await output.WriteAsync(suffix);

            var response = await input.ReadLineAsync(cancellationToken);
            if (response is null)
            {
                throw new SymphonyCliException($"{label} input ended unexpectedly.");
            }

            if (string.IsNullOrWhiteSpace(response))
            {
                return defaultValue;
            }

            if (response.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                response.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (response.Equals("n", StringComparison.OrdinalIgnoreCase) ||
                response.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            await output.WriteLineAsync("Please answer yes or no.");
        }
    }

    private static string ReadSecretFromConsole()
    {
        var builder = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key is ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key is ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }

                continue;
            }

            if (key.KeyChar != '\0')
            {
                builder.Append(key.KeyChar);
            }
        }

        return builder.ToString().Trim();
    }

    private static void EnsureUnixSecretMode(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private static void EnsureUnixModes(params string[] paths)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var path in paths.Where(File.Exists))
        {
            try
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead |
                    UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead |
                    UnixFileMode.OtherExecute);
            }
            catch (PlatformNotSupportedException)
            {
                return;
            }
        }
    }

    private static void TryCopyUnixMode(string sourcePath, string destinationPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var sourceMode = File.GetUnixFileMode(sourcePath);
            File.SetUnixFileMode(destinationPath, sourceMode);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private static string BuildInstanceId(string repo, string installFolder)
    {
        var seed = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..8].ToLowerInvariant();
        var folderName = Path.GetFileName(installFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var repoFragment = SanitizeForId(string.IsNullOrWhiteSpace(folderName) ? repo : folderName);
        return $"{repoFragment}-{seed}";
    }

    private static string SanitizeForId(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length == 0 || builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-').Length == 0 ? "symphony" : builder.ToString().Trim('-');
    }

    private static int GetSuggestedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string RenderEnvironmentFile(string token)
    {
        return $"GITHUB_TOKEN={token}{Environment.NewLine}";
    }

    private static string RenderAppSettingsJson(string instanceId)
    {
        var payload = new
        {
            Symphony = new
            {
                Polling = new { IntervalMs = SymphonyDefaults.PollIntervalMs },
                Agent = new { MaxConcurrentAgents = SymphonyDefaults.MaxConcurrentAgents }
            },
            Orchestration = new
            {
                InstanceId = instanceId,
                LeaseName = "poll-dispatch",
                LeaseTtlSeconds = 900
            },
            Persistence = new
            {
                ConnectionString = "Data Source=./data/symphony.db;Cache=Shared;Mode=ReadWriteCreate"
            },
            Logging = new
            {
                LogLevel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Default"] = "Information",
                    ["Microsoft.AspNetCore"] = "Warning"
                }
            },
            AllowedHosts = "*"
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static string RenderWorkflow(string owner, string repo, string baseBranch, string remoteUrl, int port)
    {
        return $$"""
            ---
            tracker:
              kind: github
              endpoint: https://api.github.com/graphql
              api_key: $GITHUB_TOKEN
              owner: {{YamlQuote(owner)}}
              repo: {{YamlQuote(repo)}}
              milestone: null
              include_pull_requests: true
              labels: []
              active_states:
                - Open
                - In Progress
              terminal_states:
                - Closed
            polling:
              interval_ms: 600000
            agent:
              max_concurrent_agents: 5
              max_turns: 20
              max_retry_backoff_ms: 300000
              max_concurrent_agents_by_state: {}
            server:
              port: {{port}}
            codex:
              command: codex app-server
              turn_timeout_ms: 3600000
              approval_policy: never
              thread_sandbox: danger-full-access
              turn_sandbox_policy: danger-full-access
              read_timeout_ms: 5000
              stall_timeout_ms: 300000
            workspace:
              root: ./workspaces
              shared_clone_path: ./workspaces/repo
              worktrees_root: ./workspaces/worktrees
              base_branch: {{YamlQuote(baseBranch)}}
              remote_url: {{YamlQuote(remoteUrl)}}
            hooks:
              after_create: null
              before_run: null
              after_run: null
              before_remove: null
              timeout_ms: 60000
            ---

            You are working on a GitHub issue for this repository.

            - Read the issue details, comments, linked pull requests, and attachments before making changes.
            - Implement the requested change in the current worktree with minimal, correct, and safe edits.
            - Run relevant build and test commands before finishing.
            - Publish a pull request that references the issue when the work is ready for review.
            """;
    }

    private static string RenderWindowsRunScript(string executableFileName)
    {
        return $$"""
            @echo off
            setlocal EnableExtensions
            set "SCRIPT_DIR=%~dp0"
            if exist "%SCRIPT_DIR%.env" (
              for /f "usebackq eol=# tokens=1,* delims==" %%A in ("%SCRIPT_DIR%.env") do (
                set "%%A=%%B"
              )
            )
            pushd "%SCRIPT_DIR%" >nul
            "%SCRIPT_DIR%{{executableFileName}}" %*
            set "exitCode=%ERRORLEVEL%"
            popd >nul
            exit /b %exitCode%
            """;
    }

    private static string RenderUnixRunScript(string executableFileName)
    {
        return $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
            if [[ -f "$SCRIPT_DIR/.env" ]]; then
              set -a
              # shellcheck disable=SC1091
              source "$SCRIPT_DIR/.env"
              set +a
            fi
            cd "$SCRIPT_DIR"
            exec "$SCRIPT_DIR/{{executableFileName}}" "$@"
            """;
    }

    private static string YamlQuote(string value)
    {
        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private static string GetPreferredRunCommand()
    {
        return OperatingSystem.IsWindows()
            ? ".\\run-symphony.cmd"
            : "./run-symphony.sh";
    }

    private static bool IsNestedUnder(string parentPath, string candidatePath)
    {
        var normalizedParent = EnsureTrailingSeparator(Path.GetFullPath(parentPath));
        var normalizedCandidate = EnsureTrailingSeparator(Path.GetFullPath(candidatePath));

        return normalizedCandidate.StartsWith(
            normalizedParent,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }
}
