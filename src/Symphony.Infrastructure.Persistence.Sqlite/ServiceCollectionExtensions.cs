using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Symphony.Core.Abstractions;
using Symphony.Infrastructure.Persistence.Sqlite.Storage;

namespace Symphony.Infrastructure.Persistence.Sqlite;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSymphonySqlitePersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<SqliteStorageOptions>()
            .Bind(configuration.GetSection(SqliteStorageOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString), $"{SqliteStorageOptions.SectionName}:ConnectionString must be configured.")
            .ValidateOnStart();

        services.AddDbContext<SymphonyDbContext>((serviceProvider, optionsBuilder) =>
        {
            var storageOptions = serviceProvider.GetRequiredService<IOptions<SqliteStorageOptions>>().Value;
            var hostEnvironment = serviceProvider.GetRequiredService<IHostEnvironment>();
            var connectionString = SqliteConnectionStringResolver.Resolve(
                storageOptions.ConnectionString,
                hostEnvironment.ContentRootPath);

            optionsBuilder.UseSqlite(connectionString);
        });

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IOrchestrationCoordinationStore, OrchestrationCoordinationStore>();

        return services;
    }
}
