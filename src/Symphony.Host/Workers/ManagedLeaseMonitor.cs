using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Symphony.Core.Configuration;
using Symphony.Host.Services;
using Symphony.Infrastructure.Persistence.Sqlite;

namespace Symphony.Host.Workers;

public sealed class ManagedLeaseMonitor(
    IOptions<ManagedLeaseOptions> options,
    ManagedLeaseRuntime runtime,
    IServiceScopeFactory scopeFactory,
    ILogger<ManagedLeaseMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<SymphonyDbContext>();
                    var persisted = await dbContext.ManagedLease.AsNoTracking()
                        .SingleAsync(item => item.Id == 1, stoppingToken);
                    var local = runtime.GetLocalDecision();
                    if (local.Allowed && !runtime.GetDecision(persisted).Allowed)
                    {
                        // Recheck after an in-process renewal may have committed between
                        // the read and the runtime update.
                        var confirmed = await dbContext.ManagedLease.AsNoTracking()
                            .SingleAsync(item => item.Id == 1, stoppingToken);
                        if (runtime.GetLocalDecision().Allowed && !runtime.GetDecision(confirmed).Allowed)
                        {
                            runtime.Invalidate("lease_epoch_mismatch");
                            logger.LogWarning("Managed lease epoch changed outside this process; active work was canceled.");
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    runtime.Invalidate("lease_state_unavailable");
                    logger.LogWarning(ex, "Managed lease state could not be verified; active work was canceled.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
