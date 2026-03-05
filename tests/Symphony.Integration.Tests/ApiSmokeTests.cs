using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Symphony.Integration.Tests;

public sealed class ApiSmokeTests
{
    [Fact]
    public async Task HealthEndpoint_ShouldReturnSuccess()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/health");

        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task RuntimeEndpoint_ShouldReturnConfiguredDefaults()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/runtime");
        var content = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("\"intervalMs\":600000", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"maxConcurrentAgents\":5", content, StringComparison.OrdinalIgnoreCase);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"symphony-int-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbPath};Cache=Shared;Mode=ReadWriteCreate";

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Symphony:Tracker:Owner"] = "integration-owner",
                    ["Symphony:Tracker:Repo"] = "integration-repo",
                    ["Persistence:ConnectionString"] = connectionString
                });
            });
        });
    }
}
