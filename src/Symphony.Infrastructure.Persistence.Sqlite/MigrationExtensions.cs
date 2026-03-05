using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Symphony.Infrastructure.Persistence.Sqlite;

public static class MigrationExtensions
{
    public static async Task ApplySymphonyMigrationsAsync(this IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SymphonyDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
