using Microsoft.EntityFrameworkCore;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite;

namespace Symphony.Host.Services;

// Independent of the poll lease: remains alive through worker shutdown, after_run,
// workspace cleanup and final persistence, then is removed as the last durable write.
internal sealed class WorkerLeaseHeartbeat : IAsyncDisposable
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IssueExecutionRequest request;
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource workerCancellation;
    private readonly ILogger logger;
    private readonly CancellationTokenSource stop = new();
    private readonly Task heartbeat;

    private WorkerLeaseHeartbeat(IServiceScopeFactory scopeFactory, IssueExecutionRequest request,
        TimeProvider clock, CancellationTokenSource workerCancellation, ILogger logger)
    {
        this.scopeFactory = scopeFactory;
        this.request = request;
        this.clock = clock;
        this.workerCancellation = workerCancellation;
        this.logger = logger;
        heartbeat = RunAsync();
    }

    public static async Task<WorkerLeaseHeartbeat> StartAsync(IServiceScopeFactory scopeFactory,
        IssueExecutionRequest request, TimeProvider clock, CancellationTokenSource workerCancellation, ILogger logger)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SymphonyDbContext>();
        var leaseName = WorkerLeaseNames.ForAttempt(request.RunId, request.AttemptId);
        var lease = await db.InstanceLeases.AsNoTracking().SingleOrDefaultAsync(
            row => row.LeaseName == leaseName, workerCancellation.Token);
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(row => row.Id == request.RunId, workerCancellation.Token);
        if (lease is null || lease.OwnerInstanceId != request.InstanceId || lease.ExpiresAtUtc <= clock.GetUtcNow() ||
            run is null || run.OwnerInstanceId != request.InstanceId)
        {
            throw new InvalidOperationException("Worker lifetime lease is unavailable; refusing to launch the agent.");
        }

        return new WorkerLeaseHeartbeat(scopeFactory, request, clock, workerCancellation, logger);
    }

    private async Task RunAsync()
    {
        var ttl = request.WorkerLeaseTtl ?? TimeSpan.FromMinutes(15);
        var interval = TimeSpan.FromSeconds(Math.Min(10, ttl.TotalSeconds / 3));
        using var timer = new PeriodicTimer(interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    timeout.CancelAfter(interval);
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<SymphonyDbContext>();
                    await using var transaction = await db.Database.BeginTransactionAsync(timeout.Token);
                    var leaseName = WorkerLeaseNames.ForAttempt(request.RunId, request.AttemptId);
                    var lease = await db.InstanceLeases.SingleOrDefaultAsync(row => row.LeaseName == leaseName, timeout.Token);
                    var now = clock.GetUtcNow();
                    if (lease is null || lease.OwnerInstanceId != request.InstanceId || lease.ExpiresAtUtc <= now)
                    {
                        workerCancellation.Cancel();
                        logger.LogWarning("worker_lease_lost for {IssueId} {RunId}; worker canceled.", request.Issue.Id, request.RunId);
                        return;
                    }

                    lease.UpdatedAtUtc = now;
                    lease.ExpiresAtUtc = now.Add(ttl);
                    var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(row => row.Id == request.RunId, timeout.Token);
                    await db.SaveChangesAsync(timeout.Token);
                    await transaction.CommitAsync(timeout.Token);
                    if (run is null || run.OwnerInstanceId != request.InstanceId || run.RequestedStopReason is not null)
                    {
                        workerCancellation.Cancel();
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    workerCancellation.Cancel();
                    logger.LogWarning(ex, "worker_lease_renewal_failed for {IssueId} {RunId}; worker canceled.", request.Issue.Id, request.RunId);
                    // Continue protecting finalization if database access recovers.
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await heartbeat;
        stop.Dispose();
    }

    public static async Task ReleaseAsync(IServiceScopeFactory scopeFactory, IssueExecutionRequest request)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SymphonyDbContext>();
        var leaseName = WorkerLeaseNames.ForAttempt(request.RunId, request.AttemptId);
        await db.InstanceLeases.Where(row => row.LeaseName == leaseName && row.OwnerInstanceId == request.InstanceId)
            .ExecuteDeleteAsync(CancellationToken.None);
    }
}
