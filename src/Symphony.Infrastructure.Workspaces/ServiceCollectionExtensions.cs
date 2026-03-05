using Microsoft.Extensions.DependencyInjection;
using Symphony.Core.Abstractions;

namespace Symphony.Infrastructure.Workspaces;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSymphonyWorkspaceServices(this IServiceCollection services)
    {
        services.AddSingleton<IWorkspaceManager, GitWorktreeWorkspaceManager>();
        services.AddSingleton<IWorkspaceHookRunner, WorkspaceHookRunner>();
        return services;
    }
}
