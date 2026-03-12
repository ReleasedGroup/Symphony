using Symphony.Host.Setup;

namespace Symphony.Integration.Tests;

public sealed class CodexCliPreflightEvaluatorTests
{
    [Fact]
    public async Task CheckAsync_ShouldAcceptReadyCodexCli()
    {
        var codexHome = CreateTempDirectory("codex-home");

        try
        {
            await File.WriteAllTextAsync(Path.Combine(codexHome, "auth.json"), "{}");

            var result = await CodexCliPreflightEvaluator.CheckAsync(
                CreateRunner(new Dictionary<string, CodexCliCommandResult>(StringComparer.Ordinal)
                {
                    ["codex --version"] = new(0, "codex-cli 0.114.0", string.Empty),
                    ["npm view @openai/codex version"] = new(0, "0.114.0", string.Empty),
                    ["codex login status"] = new(0, "Logged in using ChatGPT", string.Empty)
                }),
                codexHome,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            Assert.True(result.IsReadyToStart);
            Assert.Equal("0.114.0", result.InstalledVersion);
            Assert.Equal("0.114.0", result.LatestVersion);
            Assert.True(result.LatestVersionVerified);
            Assert.True(result.HasAuthJson);
            Assert.True(result.LoginConfigured);
        }
        finally
        {
            TryDeleteDirectory(codexHome);
        }
    }

    [Fact]
    public async Task CheckAsync_ShouldBlockWhenInstalledVersionIsBehindValidatedVersion()
    {
        var codexHome = CreateTempDirectory("codex-home");

        try
        {
            await File.WriteAllTextAsync(Path.Combine(codexHome, "auth.json"), "{}");

            var result = await CodexCliPreflightEvaluator.CheckAsync(
                CreateRunner(new Dictionary<string, CodexCliCommandResult>(StringComparer.Ordinal)
                {
                    ["codex --version"] = new(0, "codex-cli 0.113.0", string.Empty),
                    ["npm view @openai/codex version"] = new(0, "0.114.0", string.Empty),
                    ["codex login status"] = new(0, "Logged in using ChatGPT", string.Empty)
                }),
                codexHome,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            Assert.False(result.IsReadyToStart);
            Assert.Contains(
                result.BlockingIssues,
                issue => issue.Contains("Symphony-validated version 0.114.0", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(codexHome);
        }
    }

    [Fact]
    public async Task CheckAsync_ShouldBlockWhenAuthJsonIsMissing()
    {
        var codexHome = CreateTempDirectory("codex-home");

        try
        {
            var result = await CodexCliPreflightEvaluator.CheckAsync(
                CreateRunner(new Dictionary<string, CodexCliCommandResult>(StringComparer.Ordinal)
                {
                    ["codex --version"] = new(0, "codex-cli 0.114.0", string.Empty),
                    ["npm view @openai/codex version"] = new(0, "0.114.0", string.Empty),
                    ["codex login status"] = new(0, "Logged in using ChatGPT", string.Empty)
                }),
                codexHome,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            Assert.False(result.IsReadyToStart);
            Assert.Contains(
                result.BlockingIssues,
                issue => issue.Contains("auth file is missing", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectory(codexHome);
        }
    }

    [Fact]
    public async Task CheckAsync_ShouldFallbackToLocalVersionCacheWhenNpmLookupFails()
    {
        var codexHome = CreateTempDirectory("codex-home");

        try
        {
            await File.WriteAllTextAsync(Path.Combine(codexHome, "auth.json"), "{}");
            await File.WriteAllTextAsync(
                Path.Combine(codexHome, "version.json"),
                """
                {
                  "latest_version": "0.114.0"
                }
                """);

            var result = await CodexCliPreflightEvaluator.CheckAsync(
                CreateRunner(new Dictionary<string, CodexCliCommandResult>(StringComparer.Ordinal)
                {
                    ["codex --version"] = new(0, "codex-cli 0.114.0", string.Empty),
                    ["npm view @openai/codex version"] = new(1, string.Empty, "npm unavailable"),
                    ["codex login status"] = new(0, "Logged in using ChatGPT", string.Empty)
                }),
                codexHome,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            Assert.True(result.IsReadyToStart);
            Assert.Equal("cache", result.LatestVersionSource);
            Assert.False(result.LatestVersionVerified);
            Assert.Contains(
                result.Warnings,
                warning => warning.Contains("local Codex version cache", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectory(codexHome);
        }
    }

    [Fact]
    public async Task CheckAsync_ShouldPropagateCallerCancellation()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CodexCliPreflightEvaluator.CheckAsync(
                (_, token) => Task.FromCanceled<CodexCliCommandResult>(token),
                Path.Combine(Path.GetTempPath(), $"symphony-codex-home-{Guid.NewGuid():N}"),
                TimeSpan.FromSeconds(1),
                cancellationTokenSource.Token));
    }

    [Fact]
    public async Task CheckAsync_ShouldTreatTimedOutNpmProbeAsUnverifiedLatestVersion()
    {
        var codexHome = CreateTempDirectory("codex-home");

        try
        {
            await File.WriteAllTextAsync(Path.Combine(codexHome, "auth.json"), "{}");
            await File.WriteAllTextAsync(
                Path.Combine(codexHome, "version.json"),
                """
                {
                  "latest_version": "0.114.0"
                }
                """);

            var result = await CodexCliPreflightEvaluator.CheckAsync(
                (command, token) => command.Equals("npm view @openai/codex version", StringComparison.Ordinal)
                    ? WaitForCancellationAsync(token)
                    : Task.FromResult(command switch
                    {
                        "codex --version" => new CodexCliCommandResult(0, "codex-cli 0.114.0", string.Empty),
                        "codex login status" => new CodexCliCommandResult(0, "Logged in using ChatGPT", string.Empty),
                        _ => throw new InvalidOperationException($"Unexpected command '{command}'.")
                    }),
                codexHome,
                TimeSpan.FromMilliseconds(50),
                CancellationToken.None);

            Assert.True(result.IsReadyToStart);
            Assert.Equal("cache", result.LatestVersionSource);
            Assert.False(result.LatestVersionVerified);
        }
        finally
        {
            TryDeleteDirectory(codexHome);
        }
    }

    private static Func<string, CancellationToken, Task<CodexCliCommandResult>> CreateRunner(
        IReadOnlyDictionary<string, CodexCliCommandResult> responses)
    {
        return (command, _) =>
        {
            if (!responses.TryGetValue(command, out var response))
            {
                throw new InvalidOperationException($"No fake response configured for '{command}'.");
            }

            return Task.FromResult(response);
        };
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"symphony-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<CodexCliCommandResult> WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("Expected cancellation before completion.");
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
