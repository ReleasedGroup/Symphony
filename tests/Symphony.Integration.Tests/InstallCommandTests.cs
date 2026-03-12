using Symphony.Host;
using Symphony.Host.Setup;

namespace Symphony.Integration.Tests;

public sealed class InstallCommandTests
{
    [Fact]
    public async Task RunAsync_ShouldCreateIsolatedInstanceFilesAndCopyBundlePayload()
    {
        var bundleRoot = CreateTempDirectory("bundle");
        var installRoot = CreateTempDirectory("install");
        var executableName = OperatingSystem.IsWindows() ? "Symphony.exe" : "Symphony";

        try
        {
            Directory.CreateDirectory(Path.Combine(bundleRoot, "wwwroot"));
            Directory.CreateDirectory(Path.Combine(bundleRoot, "data"));
            await File.WriteAllTextAsync(Path.Combine(bundleRoot, executableName), "binary");
            await File.WriteAllTextAsync(Path.Combine(bundleRoot, "setup-symphony.cmd"), "@echo off");
            await File.WriteAllTextAsync(Path.Combine(bundleRoot, "setup-symphony.sh"), "#!/usr/bin/env bash");
            await File.WriteAllTextAsync(Path.Combine(bundleRoot, "wwwroot", "index.html"), "<html>ok</html>");
            await File.WriteAllTextAsync(Path.Combine(bundleRoot, "data", "stale.db"), "should-not-copy");
            await File.WriteAllTextAsync(Path.Combine(bundleRoot, ".env"), "GITHUB_TOKEN=stale");

            var input = new StringReader($"""
                github_pat_testtoken
                releasedgroup
                symphony
                main
                {installRoot}
                43123
                """);
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = await SymphonyInstallCommand.RunAsync(
                new InstallCommandOptions(NoLaunch: true, ShowHelp: false),
                input,
                output,
                error,
                CancellationToken.None,
                CreateRuntime(bundleRoot, executableName, readyToStart: true));

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, error.ToString());

            var workflow = await File.ReadAllTextAsync(Path.Combine(installRoot, "WORKFLOW.md"));
            Assert.Contains("owner: 'releasedgroup'", workflow, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("repo: 'symphony'", workflow, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("base_branch: 'main'", workflow, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("port: 43123", workflow, StringComparison.OrdinalIgnoreCase);

            var appSettings = await File.ReadAllTextAsync(Path.Combine(installRoot, "appsettings.json"));
            Assert.Contains("\"InstanceId\"", appSettings, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("symphony.db", appSettings, StringComparison.OrdinalIgnoreCase);

            var environmentFile = await File.ReadAllTextAsync(Path.Combine(installRoot, ".env"));
            Assert.Equal($"GITHUB_TOKEN=github_pat_testtoken{Environment.NewLine}", environmentFile);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(installRoot, ".env")));
            }

            Assert.True(File.Exists(Path.Combine(installRoot, executableName)));
            Assert.True(File.Exists(Path.Combine(installRoot, "wwwroot", "index.html")));
            Assert.False(File.Exists(Path.Combine(installRoot, "data", "stale.db")));
            Assert.True(File.Exists(Path.Combine(installRoot, OperatingSystem.IsWindows() ? "run-symphony.cmd" : "run-symphony.sh")));
            Assert.Contains("http://127.0.0.1:43123/", output.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(bundleRoot);
            TryDeleteDirectory(installRoot);
        }
    }

    [Fact]
    public async Task RunAsync_ShouldFailFastWhenTokenInputEndsUnexpectedly()
    {
        var bundleRoot = CreateTempDirectory("bundle");
        var executableName = OperatingSystem.IsWindows() ? "Symphony.exe" : "Symphony";

        try
        {
            await File.WriteAllTextAsync(Path.Combine(bundleRoot, executableName), "binary");
            await File.WriteAllTextAsync(Path.Combine(bundleRoot, "setup-symphony.cmd"), "@echo off");
            await File.WriteAllTextAsync(Path.Combine(bundleRoot, "setup-symphony.sh"), "#!/usr/bin/env bash");

            var input = new StringReader(string.Empty);
            var ex = await Assert.ThrowsAsync<SymphonyCliException>(() =>
                SymphonyInstallCommand.RunAsync(
                    new InstallCommandOptions(NoLaunch: true, ShowHelp: false),
                    input,
                    TextWriter.Null,
                    TextWriter.Null,
                    CancellationToken.None,
                    CreateRuntime(bundleRoot, executableName, readyToStart: true)));

            Assert.Contains("GitHub token input ended unexpectedly.", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(bundleRoot);
        }
    }

    [Fact]
    public async Task RunAsync_ShouldFailFastWhenRequiredTextInputEndsUnexpectedly()
    {
        var bundleRoot = CreateTempDirectory("bundle");
        var executableName = OperatingSystem.IsWindows() ? "Symphony.exe" : "Symphony";

        try
        {
            await File.WriteAllTextAsync(Path.Combine(bundleRoot, executableName), "binary");
            await File.WriteAllTextAsync(Path.Combine(bundleRoot, "setup-symphony.cmd"), "@echo off");
            await File.WriteAllTextAsync(Path.Combine(bundleRoot, "setup-symphony.sh"), "#!/usr/bin/env bash");

            var input = new StringReader("github_pat_testtoken");
            var ex = await Assert.ThrowsAsync<SymphonyCliException>(() =>
                SymphonyInstallCommand.RunAsync(
                    new InstallCommandOptions(NoLaunch: true, ShowHelp: false),
                    input,
                    TextWriter.Null,
                    TextWriter.Null,
                    CancellationToken.None,
                    CreateRuntime(bundleRoot, executableName, readyToStart: true)));

            Assert.Contains("GitHub owner input ended unexpectedly.", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(bundleRoot);
        }
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--HELP")]
    [InlineData("-h")]
    [InlineData("-H")]
    public void InstallCommandOptions_Parse_ShouldAcceptHelpCaseInsensitively(string option)
    {
        var parsed = InstallCommandOptions.Parse([option]);

        Assert.True(parsed.ShowHelp);
    }

    [Fact]
    public async Task RunAsync_ShouldBlockLaunchWhenCodexCliIsNotReady()
    {
        var bundleRoot = CreateTempDirectory("bundle");
        var installRoot = CreateTempDirectory("install");
        var executableName = OperatingSystem.IsWindows() ? "Symphony.exe" : "Symphony";

        try
        {
            await File.WriteAllTextAsync(Path.Combine(bundleRoot, executableName), "binary");
            await File.WriteAllTextAsync(Path.Combine(bundleRoot, "setup-symphony.cmd"), "@echo off");
            await File.WriteAllTextAsync(Path.Combine(bundleRoot, "setup-symphony.sh"), "#!/usr/bin/env bash");

            var input = new StringReader($"""
                github_pat_testtoken
                releasedgroup
                symphony
                main
                {installRoot}
                43123
                n
                """);
            var output = new StringWriter();

            var exitCode = await SymphonyInstallCommand.RunAsync(
                new InstallCommandOptions(NoLaunch: false, ShowHelp: false),
                input,
                output,
                TextWriter.Null,
                CancellationToken.None,
                CreateRuntime(bundleRoot, executableName, readyToStart: false));

            Assert.Equal(1, exitCode);
            Assert.Contains("Codex CLI check:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Fix the Codex CLI items above before Symphony starts.", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Installation completed, but Symphony was not started.", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(bundleRoot);
            TryDeleteDirectory(installRoot);
        }
    }

    [Fact]
    public async Task RunAsync_ShouldReportCodexCliIssuesForNoLaunchInstalls()
    {
        var bundleRoot = CreateTempDirectory("bundle");
        var installRoot = CreateTempDirectory("install");
        var executableName = OperatingSystem.IsWindows() ? "Symphony.exe" : "Symphony";

        try
        {
            await File.WriteAllTextAsync(Path.Combine(bundleRoot, executableName), "binary");
            await File.WriteAllTextAsync(Path.Combine(bundleRoot, "setup-symphony.cmd"), "@echo off");
            await File.WriteAllTextAsync(Path.Combine(bundleRoot, "setup-symphony.sh"), "#!/usr/bin/env bash");

            var input = new StringReader($"""
                github_pat_testtoken
                releasedgroup
                symphony
                main
                {installRoot}
                43123
                """);
            var output = new StringWriter();

            var exitCode = await SymphonyInstallCommand.RunAsync(
                new InstallCommandOptions(NoLaunch: true, ShowHelp: false),
                input,
                output,
                TextWriter.Null,
                CancellationToken.None,
                CreateRuntime(bundleRoot, executableName, readyToStart: false));

            Assert.Equal(0, exitCode);
            Assert.Contains("Before your first run, fix the Codex CLI items above", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(bundleRoot);
            TryDeleteDirectory(installRoot);
        }
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"symphony-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static SymphonyInstallationRuntime CreateRuntime(string bundleRoot, string executableName, bool readyToStart)
    {
        return new SymphonyInstallationRuntime(bundleRoot, executableName, "setup-symphony.cmd", "setup-symphony.sh")
        {
            CodexCliPreflightAsync = _ => Task.FromResult(CreatePreflightResult(readyToStart))
        };
    }

    private static CodexCliPreflightResult CreatePreflightResult(bool readyToStart)
    {
        return readyToStart
            ? new CodexCliPreflightResult(
                InstalledVersion: "0.114.0",
                ValidatedVersion: "0.114.0",
                LatestVersion: "0.114.0",
                LatestVersionSource: "npm",
                LatestVersionVerified: true,
                AuthJsonPath: Path.Combine(Path.GetTempPath(), ".codex", "auth.json"),
                HasAuthJson: true,
                LoginConfigured: true,
                BlockingIssues: [],
                Warnings: [],
                RemediationSteps: [])
            : new CodexCliPreflightResult(
                InstalledVersion: "0.113.0",
                ValidatedVersion: "0.114.0",
                LatestVersion: "0.114.0",
                LatestVersionSource: "npm",
                LatestVersionVerified: true,
                AuthJsonPath: Path.Combine(Path.GetTempPath(), ".codex", "auth.json"),
                HasAuthJson: false,
                LoginConfigured: false,
                BlockingIssues:
                [
                    "Codex CLI 0.113.0 is older than the Symphony-validated version 0.114.0.",
                    $"Codex auth file is missing: '{Path.Combine(Path.GetTempPath(), ".codex", "auth.json")}'."
                ],
                Warnings: [],
                RemediationSteps:
                [
                    "Update Codex CLI with `npm install -g @openai/codex@0.114.0`.",
                    "Run `codex login` in another terminal, finish the sign-in flow, then return here."
                ]);
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
        }
    }
}
