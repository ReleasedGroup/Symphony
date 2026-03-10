using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Symphony.Core.Configuration;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Infrastructure.Workflows;

public sealed class WorkflowDefinitionProvider(
    WorkflowLoader loader,
    IOptions<WorkflowLoaderOptions> options,
    ILogger<WorkflowDefinitionProvider> logger) : IWorkflowDefinitionProvider, IDisposable
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _watcherLock = new();
    private WorkflowDefinition? _lastKnownGood;
    private WorkflowFileStamp _lastProcessedStamp;
    private FileSystemWatcher? _watcher;
    private string? _watchedPath;
    private volatile bool _reloadRequested = true;

    public async Task<WorkflowDefinition> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var path = WorkflowPathResolver.Resolve(options.Value.Path);
        EnsureChangeWatcher(path);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var currentStamp = GetWorkflowFileStamp(path);
            if (_lastKnownGood is not null && !_reloadRequested && _lastProcessedStamp == currentStamp)
            {
                return _lastKnownGood;
            }

            try
            {
                var loaded = await loader.LoadAsync(path, cancellationToken);
                _lastKnownGood = loaded;
                _lastProcessedStamp = currentStamp;
                _reloadRequested = false;
                logger.LogInformation("Loaded workflow from {WorkflowPath} at {LoadedAtUtc}.", path, loaded.LoadedAtUtc);
                return loaded;
            }
            catch (WorkflowLoadException ex)
            {
                _lastProcessedStamp = currentStamp;
                _reloadRequested = false;

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

    public void Dispose()
    {
        lock (_watcherLock)
        {
            _watcher?.Dispose();
        }

        _lock.Dispose();
    }

    private void EnsureChangeWatcher(string path)
    {
        lock (_watcherLock)
        {
            if (string.Equals(_watchedPath, path, PathComparison))
            {
                return;
            }

            _watcher?.Dispose();
            _watcher = null;
            _watchedPath = path;
            _reloadRequested = true;

            var directory = Path.GetDirectoryName(path);
            var fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName) || !Directory.Exists(directory))
            {
                return;
            }

            var watcher = new FileSystemWatcher(directory, fileName)
            {
                NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            watcher.Changed += OnWorkflowFileChanged;
            watcher.Created += OnWorkflowFileChanged;
            watcher.Deleted += OnWorkflowFileChanged;
            watcher.Renamed += OnWorkflowFileRenamed;
            _watcher = watcher;
        }
    }

    private static WorkflowFileStamp GetWorkflowFileStamp(string path)
    {
        if (!File.Exists(path))
        {
            return new WorkflowFileStamp(Exists: false, LastWriteTimeUtc: DateTimeOffset.MinValue, Length: -1);
        }

        var fileInfo = new FileInfo(path);
        return new WorkflowFileStamp(
            Exists: true,
            LastWriteTimeUtc: fileInfo.LastWriteTimeUtc,
            Length: fileInfo.Length);
    }

    private void OnWorkflowFileChanged(object sender, FileSystemEventArgs args)
    {
        _reloadRequested = true;
        logger.LogInformation(
            "Detected workflow change for {WorkflowPath}. Reload will apply on the next access.",
            _watchedPath ?? args.FullPath);
    }

    private void OnWorkflowFileRenamed(object sender, RenamedEventArgs args)
    {
        _reloadRequested = true;
        logger.LogInformation(
            "Detected workflow rename for {WorkflowPath}. Reload will apply on the next access.",
            _watchedPath ?? args.FullPath);
    }

    private readonly record struct WorkflowFileStamp(bool Exists, DateTimeOffset LastWriteTimeUtc, long Length);
}
