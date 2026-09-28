using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Symphony.Host;
using Symphony.Host.Setup;

namespace Symphony.Integration.Tests;

public sealed class ManagedInstallTests
{
    [Fact]
    public async Task ManagedInstallCli_ShouldReturnJsonForMissingOptions()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SymphonyHostApplication.RunCliAsync(
            ["install", "--managed"], error, standardOutput: output);
        Assert.Equal(2, exit);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Equal("missing_option", JsonDocument.Parse(output.ToString()).RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ManagedInstall_ShouldPreflightWithoutWritingThenInstallWithoutLaunching()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            var preflightOutput = new StringWriter();
            var preflightExit = await SymphonyManagedInstallCommand.RunAsync(
                [.. fixture.Arguments, "--preflight-only"], preflightOutput, CancellationToken.None, fixture.Runtime);
            Assert.Equal(0, preflightExit);
            using (var result = JsonDocument.Parse(preflightOutput.ToString()))
            {
                Assert.Equal("ready", result.RootElement.GetProperty("status").GetString());
                Assert.Equal("fixed-device-01", result.RootElement.GetProperty("instanceId").GetString());
            }
            Assert.False(Directory.Exists(fixture.Target));

            var output = new StringWriter();
            var installExit = await SymphonyManagedInstallCommand.RunAsync(
                fixture.Arguments, output, CancellationToken.None, fixture.Runtime);
            Assert.Equal(0, installExit);
            using var installed = JsonDocument.Parse(output.ToString());
            Assert.Equal("installed", installed.RootElement.GetProperty("status").GetString());
            Assert.False(installed.RootElement.GetProperty("started").GetBoolean());
            Assert.Equal(fixture.Port, installed.RootElement.GetProperty("port").GetInt32());
            Assert.DoesNotContain("github_pat_fake-secret", output.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(fixture.Target, ".env")));
            Assert.True(File.Exists(Path.Combine(fixture.Target, "Symphony" + (OperatingSystem.IsWindows() ? ".exe" : ""))));
            Assert.True(File.Exists(Path.Combine(fixture.Target, "wwwroot", "index.html")));
            var config = await File.ReadAllTextAsync(Path.Combine(fixture.Target, "appsettings.json"));
            Assert.Contains("fixed-device-01", config, StringComparison.Ordinal);
            Assert.Contains("symphony.db", config, StringComparison.Ordinal);
            var workflow = await File.ReadAllTextAsync(Path.Combine(fixture.Target, "WORKFLOW.md"));
            Assert.Contains("api_key: $SYMPHONY_GITHUB_TOKEN", workflow, StringComparison.Ordinal);

            var retry = new StringWriter();
            var retryExit = await SymphonyManagedInstallCommand.RunAsync(
                fixture.Arguments, retry, CancellationToken.None, fixture.Runtime);
            Assert.Equal(2, retryExit);
            Assert.Equal("directory_collision", JsonDocument.Parse(retry.ToString()).RootElement.GetProperty("code").GetString());
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task ManagedInstall_ShouldRejectPortCollisionAndLeaveTargetUntouched()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, fixture.Port);
            listener.Start();
            var output = new StringWriter();
            var exit = await SymphonyManagedInstallCommand.RunAsync(
                [.. fixture.Arguments, "--preflight-only"], output, CancellationToken.None, fixture.Runtime);
            Assert.Equal(2, exit);
            Assert.Equal("port_collision", JsonDocument.Parse(output.ToString()).RootElement.GetProperty("code").GetString());
            Assert.False(Directory.Exists(fixture.Target));
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task ManagedInstall_ShouldRejectInlineSecretAndNeverReturnIt()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            await File.WriteAllTextAsync(fixture.WorkflowPath,
                (await File.ReadAllTextAsync(fixture.WorkflowPath)).Replace("$SYMPHONY_GITHUB_TOKEN", "github_pat_fake-secret", StringComparison.Ordinal));
            var output = new StringWriter();
            var exit = await SymphonyManagedInstallCommand.RunAsync(
                [.. fixture.Arguments, "--preflight-only"], output, CancellationToken.None, fixture.Runtime);
            Assert.Equal(2, exit);
            Assert.Equal("invalid_secret_reference", JsonDocument.Parse(output.ToString()).RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain("github_pat_fake-secret", output.ToString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(fixture.Target));
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task ManagedInstall_ShouldReportCodexPrerequisiteWithoutExposingAuthPath()
    {
        var fixture = await Fixture.CreateAsync(codexReady: false);
        try
        {
            var output = new StringWriter();
            var exit = await SymphonyManagedInstallCommand.RunAsync(
                [.. fixture.Arguments, "--launch"], output, CancellationToken.None, fixture.Runtime);
            Assert.Equal(2, exit);
            Assert.Equal("codex_not_ready", JsonDocument.Parse(output.ToString()).RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain("private-auth-path", output.ToString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(fixture.Target));
        }
        finally { fixture.Dispose(); }
    }

    [Fact]
    public async Task ManagedInstall_ShouldRequireReferencedSecretBeforeLaunch()
    {
        var fixture = await Fixture.CreateAsync();
        try
        {
            var output = new StringWriter();
            var exit = await SymphonyManagedInstallCommand.RunAsync(
                [.. fixture.Arguments, "--launch"], output, CancellationToken.None, fixture.Runtime);
            Assert.Equal(2, exit);
            Assert.Equal("secret_not_available", JsonDocument.Parse(output.ToString()).RootElement.GetProperty("code").GetString());
            Assert.False(Directory.Exists(fixture.Target));
        }
        finally { fixture.Dispose(); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        public string Target { get; }
        public string WorkflowPath { get; }
        public int Port { get; }
        public string[] Arguments { get; }
        public SymphonyInstallationRuntime Runtime { get; }

        private Fixture(string root, string target, string workflowPath, int port, SymphonyInstallationRuntime runtime)
        {
            _root = root;
            Target = target;
            WorkflowPath = workflowPath;
            Port = port;
            Runtime = runtime;
            Arguments = ["--instance-id", "fixed-device-01", "--instance-dir", target,
                "--workflow-path", workflowPath, "--config-path", Path.Combine(root, "source", "config.json"),
                "--port", port.ToString(), "--github-token-env", "SYMPHONY_GITHUB_TOKEN"];
        }

        public static async Task<Fixture> CreateAsync(bool codexReady = true)
        {
            var root = Path.Combine(Path.GetTempPath(), $"symphony-managed-{Guid.NewGuid():N}");
            var bundle = Path.Combine(root, "bundle");
            var source = Path.Combine(root, "source");
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(bundle);
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(Path.Combine(bundle, "wwwroot"));
            var executable = OperatingSystem.IsWindows() ? "Symphony.exe" : "Symphony";
            await File.WriteAllTextAsync(Path.Combine(bundle, executable), "binary");
            await File.WriteAllTextAsync(Path.Combine(bundle, "wwwroot", "index.html"), "ok");
            await File.WriteAllTextAsync(Path.Combine(bundle, ".env"), "GITHUB_TOKEN=stale");
            var workflow = Path.Combine(source, "workflow.md");
            await File.WriteAllTextAsync(workflow,
                "---\ntracker:\n  kind: github\n  api_key: $SYMPHONY_GITHUB_TOKEN\n  owner: released\n  repo: symphony\n---\nPrompt body\n");
            await File.WriteAllTextAsync(Path.Combine(source, "config.json"), "{\"Orchestration\":{\"InstanceId\":\"old\"}}");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var runtime = new SymphonyInstallationRuntime(bundle, executable, "setup-symphony.cmd", "setup-symphony.sh")
            {
                CodexCliPreflightAsync = _ => Task.FromResult(new CodexCliPreflightResult(
                    "0.135.0", "0.135.0", "0.135.0", "test", true,
                    "private-auth-path", codexReady, codexReady,
                    codexReady ? [] : ["private-auth-path is unavailable"], [], []))
            };
            return new Fixture(root, target, workflow, port, runtime);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
        }
    }
}
