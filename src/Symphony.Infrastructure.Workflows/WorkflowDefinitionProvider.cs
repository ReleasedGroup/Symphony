using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Symphony.Core.Configuration;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Infrastructure.Workflows;

public sealed class WorkflowDefinitionProvider(
    WorkflowLoader loader,
    IOptions<WorkflowLoaderOptions> options,
    IHostEnvironment hostEnvironment,
    ILogger<WorkflowDefinitionProvider> logger) : IWorkflowDefinitionProvider
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private WorkflowDefinition? _lastKnownGood;
    private DateTimeOffset _lastLoadedWriteTimeUtc;

    public async Task<WorkflowDefinition> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var path = ResolveWorkflowPath(options.Value.Path, hostEnvironment.ContentRootPath);
        var writeTimeUtc = File.Exists(path)
            ? File.GetLastWriteTimeUtc(path)
            : DateTime.MinValue;

        if (_lastKnownGood is not null && writeTimeUtc <= _lastLoadedWriteTimeUtc)
        {
            return _lastKnownGood;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            writeTimeUtc = File.Exists(path)
                ? File.GetLastWriteTimeUtc(path)
                : DateTime.MinValue;

            if (_lastKnownGood is not null && writeTimeUtc <= _lastLoadedWriteTimeUtc)
            {
                return _lastKnownGood;
            }

            try
            {
                var loaded = await loader.LoadAsync(path, cancellationToken);
                _lastKnownGood = loaded;
                _lastLoadedWriteTimeUtc = writeTimeUtc;
                logger.LogInformation("Loaded workflow from {WorkflowPath} at {LoadedAtUtc}.", path, loaded.LoadedAtUtc);
                return loaded;
            }
            catch (WorkflowLoadException ex)
            {
                if (_lastKnownGood is not null)
                {
                    logger.LogError(
                        ex,
                        "Workflow reload failed with code '{Code}'. Keeping last known good workflow from {LoadedAtUtc}.",
                        ex.Code,
                        _lastKnownGood.LoadedAtUtc);
                    return _lastKnownGood;
                }

                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string ResolveWorkflowPath(string configuredPath, string contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = "WORKFLOW.md";
        }

        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        return Path.GetFullPath(Path.Combine(contentRootPath, configuredPath));
    }
}
