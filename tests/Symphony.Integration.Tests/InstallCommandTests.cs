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
                releasedgroup
                symphony
                github_pat_testtoken
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
                new SymphonyInstallationRuntime(bundleRoot, executableName, "setup-symphony.cmd", "setup-symphony.sh"));

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

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"symphony-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
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
