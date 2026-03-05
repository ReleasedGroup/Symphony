using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Symphony.Core.Configuration;

namespace Symphony.Infrastructure.Workflows;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSymphonyWorkflowServices(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<WorkflowLoaderOptions>()
            .Bind(configuration.GetSection(WorkflowLoaderOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Path), "Workflow:Path must be configured.")
            .ValidateOnStart();

        services.AddSingleton<WorkflowLoader>();
        services.AddSingleton<IWorkflowDefinitionProvider, WorkflowDefinitionProvider>();
        services.AddSingleton<IWorkflowPromptRenderer, WorkflowPromptRenderer>();

        return services;
    }
}
