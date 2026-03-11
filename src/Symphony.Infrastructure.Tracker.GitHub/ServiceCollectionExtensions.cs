using Microsoft.Extensions.DependencyInjection;
using Symphony.Core.Abstractions;

namespace Symphony.Infrastructure.Tracker.GitHub;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSymphonyGitHubTrackerClient(this IServiceCollection services)
    {
        services.AddHttpClient<GitHubTrackerClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddScoped<ITrackerClient>(provider => provider.GetRequiredService<GitHubTrackerClient>());
        services.AddScoped<IGitHubTrackerClient>(provider => provider.GetRequiredService<GitHubTrackerClient>());

        return services;
    }
}
