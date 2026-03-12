using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Symphony.Host.Setup;

internal static class CodexCliPreflightEvaluator
{
    internal const string ValidatedNpmVersion = "0.114.0";
    private const string CodexPackageName = "@openai/codex";
    private const string AuthFileName = "auth.json";
    private const string VersionCacheFileName = "version.json";

    public static Task<CodexCliPreflightResult> CheckAsync(CancellationToken cancellationToken)
    {
        return CheckAsync(RunCommandAsync, ResolveCodexHomePath(), cancellationToken);
    }

    internal static async Task<CodexCliPreflightResult> CheckAsync(
        Func<string, CancellationToken, Task<CodexCliCommandResult>> runCommandAsync,
        string codexHomePath,
        CancellationToken cancellationToken)
    {
        var blockingIssues = new List<string>();
        var warnings = new List<string>();
        var remediationSteps = new List<string>();
        var authJsonPath = Path.Combine(codexHomePath, AuthFileName);
        var hasAuthJson = File.Exists(authJsonPath);
        var validatedVersion = CodexCliVersion.Parse(ValidatedNpmVersion);

        string? installedVersionText = null;
        string? latestVersionText = null;
        string? latestVersionSource = null;
        var latestVersionVerified = false;
        var loginConfigured = false;
        CodexCliVersion installedVersion = default;

        var installedVersionResult = await SafeRunAsync(runCommandAsync, "codex --version", cancellationToken);
        var hasInstalledVersion = installedVersionResult is { ExitCode: 0 } &&
            CodexCliVersion.TryParse(installedVersionResult.StandardOutput, out installedVersion);

        if (!hasInstalledVersion)
        {
            blockingIssues.Add("Codex CLI is not installed or `codex --version` did not report a usable version.");
            AddUnique(
                remediationSteps,
                $"Install Codex CLI with `npm install -g {CodexPackageName}@{ValidatedNpmVersion}`.");
        }
        else
        {
            installedVersionText = installedVersion.ToString();

            if (installedVersion.CompareTo(validatedVersion) < 0)
            {
                blockingIssues.Add(
                    $"Codex CLI {installedVersionText} is older than the Symphony-validated version {validatedVersion}.");
                AddUnique(
                    remediationSteps,
                    $"Update Codex CLI with `npm install -g {CodexPackageName}@{ValidatedNpmVersion}`.");
            }

            var latestVersion = await ResolveLatestVersionAsync(runCommandAsync, codexHomePath, cancellationToken);
            if (latestVersion.Version is not null)
            {
                latestVersionText = latestVersion.Version.Value.ToString();
                latestVersionSource = latestVersion.Source;
                latestVersionVerified = latestVersion.Source is "npm";

                if (installedVersion.CompareTo(latestVersion.Version.Value) < 0)
                {
                    blockingIssues.Add(
                        $"Codex CLI {installedVersionText} is behind the latest available version {latestVersionText}.");
                    AddUnique(
                        remediationSteps,
                        $"Update Codex CLI with `npm install -g {CodexPackageName}@{latestVersionText}`.");
                }
                else if (!latestVersionVerified)
                {
                    warnings.Add(
                        $"Could not verify the latest Codex CLI version from npm; used the local Codex version cache instead ({latestVersionText}).");
                }
            }
            else
            {
                warnings.Add(
                    "Could not verify the latest Codex CLI version from npm or the local Codex version cache.");
            }

            var loginStatus = await SafeRunAsync(runCommandAsync, "codex login status", cancellationToken);
            loginConfigured = loginStatus is { ExitCode: 0 };
            if (!loginConfigured)
            {
                blockingIssues.Add("Codex authentication is not configured or `codex login status` failed.");
                AddUnique(
                    remediationSteps,
                    "Run `codex login` in another terminal, finish the sign-in flow, then return here.");
            }
        }

        if (!hasAuthJson)
        {
            blockingIssues.Add($"Codex auth file is missing: '{authJsonPath}'.");
            AddUnique(
                remediationSteps,
                $"Make sure Codex writes credentials to '{authJsonPath}' before starting Symphony.");
        }

        return new CodexCliPreflightResult(
            installedVersionText,
            validatedVersion.ToString(),
            latestVersionText,
            latestVersionSource,
            latestVersionVerified,
            authJsonPath,
            hasAuthJson,
            loginConfigured,
            blockingIssues,
            warnings,
            remediationSteps);
    }

    private static async Task<CodexCliCommandResult?> SafeRunAsync(
        Func<string, CancellationToken, Task<CodexCliCommandResult>> runCommandAsync,
        string command,
        CancellationToken cancellationToken)
    {
        try
        {
            return await runCommandAsync(command, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(CodexCliVersion? Version, string? Source)> ResolveLatestVersionAsync(
        Func<string, CancellationToken, Task<CodexCliCommandResult>> runCommandAsync,
        string codexHomePath,
        CancellationToken cancellationToken)
    {
        var npmResult = await SafeRunAsync(runCommandAsync, $"npm view {CodexPackageName} version", cancellationToken);
        if (npmResult is { ExitCode: 0 } &&
            CodexCliVersion.TryParse(npmResult.StandardOutput, out var npmVersion))
        {
            return (npmVersion, "npm");
        }

        var versionCachePath = Path.Combine(codexHomePath, VersionCacheFileName);
        if (!File.Exists(versionCachePath))
        {
            return (null, null);
        }

        try
        {
            await using var versionCacheStream = File.OpenRead(versionCachePath);
            using var document = await JsonDocument.ParseAsync(versionCacheStream, cancellationToken: cancellationToken);
            if (document.RootElement.TryGetProperty("latest_version", out var latestVersionProperty) &&
                latestVersionProperty.ValueKind is JsonValueKind.String &&
                CodexCliVersion.TryParse(latestVersionProperty.GetString(), out var cachedVersion))
            {
                return (cachedVersion, "cache");
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }

        return (null, null);
    }

    private static async Task<CodexCliCommandResult> RunCommandAsync(string command, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = BuildProcessStartInfo(command)
        };

        if (!process.Start())
        {
            return new CodexCliCommandResult(-1, string.Empty, $"Failed to start command '{command}'.");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return new CodexCliCommandResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);
    }

    private static ProcessStartInfo BuildProcessStartInfo(string command)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);
            return startInfo;
        }

        startInfo.FileName = "/bin/bash";
        startInfo.ArgumentList.Add("-lc");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private static string ResolveCodexHomePath()
    {
        var configuredCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configuredCodexHome))
        {
            return Path.GetFullPath(configuredCodexHome);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return Path.Combine(userProfile, ".codex");
        }

        return Path.Combine(Environment.CurrentDirectory, ".codex");
    }

    private static void AddUnique(ICollection<string> items, string value)
    {
        if (!items.Contains(value))
        {
            items.Add(value);
        }
    }
}

internal sealed record CodexCliPreflightResult(
    string? InstalledVersion,
    string ValidatedVersion,
    string? LatestVersion,
    string? LatestVersionSource,
    bool LatestVersionVerified,
    string AuthJsonPath,
    bool HasAuthJson,
    bool LoginConfigured,
    IReadOnlyList<string> BlockingIssues,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> RemediationSteps)
{
    public bool IsReadyToStart => BlockingIssues.Count == 0;
}

internal sealed record CodexCliCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

internal readonly record struct CodexCliVersion(
    int Major,
    int Minor,
    int Patch,
    string? Suffix) : IComparable<CodexCliVersion>
{
    public static CodexCliVersion Parse(string value)
    {
        return TryParse(value, out var version)
            ? version
            : throw new FormatException($"'{value}' is not a valid Codex CLI version.");
    }

    public static bool TryParse(string? value, out CodexCliVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var token = value
            .Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(segment => char.IsDigit(segment[0]));

        if (token is null)
        {
            return false;
        }

        var plusIndex = token.IndexOf('+');
        if (plusIndex >= 0)
        {
            token = token[..plusIndex];
        }

        string? suffix = null;
        var dashIndex = token.IndexOf('-');
        if (dashIndex >= 0)
        {
            suffix = token[(dashIndex + 1)..];
            token = token[..dashIndex];
        }

        var parts = token.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor))
        {
            return false;
        }

        var patch = 0;
        if (parts.Length == 3 && !int.TryParse(parts[2], out patch))
        {
            return false;
        }

        version = new CodexCliVersion(major, minor, patch, string.IsNullOrWhiteSpace(suffix) ? null : suffix);
        return true;
    }

    public int CompareTo(CodexCliVersion other)
    {
        var majorCompare = Major.CompareTo(other.Major);
        if (majorCompare != 0)
        {
            return majorCompare;
        }

        var minorCompare = Minor.CompareTo(other.Minor);
        if (minorCompare != 0)
        {
            return minorCompare;
        }

        var patchCompare = Patch.CompareTo(other.Patch);
        if (patchCompare != 0)
        {
            return patchCompare;
        }

        if (string.IsNullOrWhiteSpace(Suffix) && string.IsNullOrWhiteSpace(other.Suffix))
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(Suffix))
        {
            return 1;
        }

        if (string.IsNullOrWhiteSpace(other.Suffix))
        {
            return -1;
        }

        return string.Compare(Suffix, other.Suffix, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Suffix)
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{Suffix}";
    }
}
