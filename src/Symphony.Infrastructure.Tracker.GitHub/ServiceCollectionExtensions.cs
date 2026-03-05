using Microsoft.Extensions.DependencyInjection;

namespace Symphony.Infrastructure.Tracker.GitHub;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSymphonyGitHubTrackerClient(this IServiceCollection services)
    {
        services.AddHttpClient<IGitHubTrackerClient, GitHubTrackerClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }
}
