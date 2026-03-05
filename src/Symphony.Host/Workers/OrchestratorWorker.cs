using Microsoft.Extensions.Options;
using Symphony.Core.Configuration;
using Symphony.Host.Services;

namespace Symphony.Host.Workers;

public sealed class OrchestratorWorker(
    ILogger<OrchestratorWorker> logger,
    IServiceScopeFactory serviceScopeFactory,
    IOptionsMonitor<SymphonyRuntimeOptions> runtimeOptions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Orchestrator worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var pollIntervalMs = runtimeOptions.CurrentValue.Polling.IntervalMs;

            try
            {
                await using var scope = serviceScopeFactory.CreateAsyncScope();
                var tickService = scope.ServiceProvider.GetRequiredService<OrchestrationTickService>();
                var workflowPollIntervalMs = await tickService.RunTickAsync(stoppingToken);
                if (workflowPollIntervalMs is > 0)
                {
                    pollIntervalMs = workflowPollIntervalMs.Value;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Orchestrator tick failed.");
            }

            var pollInterval = TimeSpan.FromMilliseconds(pollIntervalMs);
            try
            {
                await Task.Delay(pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
