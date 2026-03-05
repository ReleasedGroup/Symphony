using Microsoft.Extensions.Options;
using Symphony.Core.Configuration;

namespace Symphony.Host.Workers;

public sealed class OrchestratorWorker(
    ILogger<OrchestratorWorker> logger,
    IOptions<SymphonyRuntimeOptions> runtimeOptions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = TimeSpan.FromMilliseconds(runtimeOptions.Value.Polling.IntervalMs);
        logger.LogInformation(
            "Orchestrator worker started. Poll interval: {PollIntervalMs} ms. Max agents: {MaxConcurrentAgents}.",
            runtimeOptions.Value.Polling.IntervalMs,
            runtimeOptions.Value.Agent.MaxConcurrentAgents);

        using var timer = new PeriodicTimer(pollInterval);
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            logger.LogInformation("Orchestrator poll tick at {UtcNow}.", DateTimeOffset.UtcNow);
        }
    }
}
