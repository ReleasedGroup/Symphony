using Microsoft.Extensions.DependencyInjection;
using Symphony.Core.Abstractions;

namespace Symphony.Infrastructure.Agent.Codex;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSymphonyCodexAgentRunner(this IServiceCollection services)
    {
        services.AddScoped<IAgentRunner, CodexAgentRunner>();
        return services;
    }
}
