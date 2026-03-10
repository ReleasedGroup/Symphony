using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Symphony.Core.Abstractions;
using Symphony.Core.Configuration;
using Symphony.Core.Models;
using Symphony.Infrastructure.Persistence.Sqlite;
using Symphony.Infrastructure.Tracker.GitHub;
using Symphony.Infrastructure.Workflows;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

public sealed partial class OrchestrationTickService
{
    private readonly IWorkflowDefinitionProvider workflowDefinitionProvider;
    private readonly IGitHubTrackerClient trackerClient;
    private readonly IOrchestrationCoordinationStore coordinationStore;
    private readonly SymphonyDbContext dbContext;
    private readonly IWorkspaceManager workspaceManager;
    private readonly IIssueExecutionCoordinator issueExecutionCoordinator;
    private readonly OrchestrationOptions orchestrationOptions;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<OrchestrationTickService> logger;

    public OrchestrationTickService(
        IWorkflowDefinitionProvider workflowDefinitionProvider,
        IGitHubTrackerClient trackerClient,
        IOrchestrationCoordinationStore coordinationStore,
        SymphonyDbContext dbContext,
        IWorkspaceManager workspaceManager,
        IIssueExecutionCoordinator issueExecutionCoordinator,
        IOptions<OrchestrationOptions> orchestrationOptions,
        TimeProvider timeProvider,
        ILogger<OrchestrationTickService> logger)
    {
        this.workflowDefinitionProvider = workflowDefinitionProvider;
        this.trackerClient = trackerClient;
        this.coordinationStore = coordinationStore;
        this.dbContext = dbContext;
        this.workspaceManager = workspaceManager;
        this.issueExecutionCoordinator = issueExecutionCoordinator;
        this.orchestrationOptions = orchestrationOptions.Value;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    public async Task RunStartupCleanupAsync(CancellationToken cancellationToken)
    {
        var workflowDefinition = await workflowDefinitionProvider.GetCurrentAsync(cancellationToken);
        await PersistWorkflowSnapshotAsync(workflowDefinition, cancellationToken);

        string apiKey;
        try
        {
            apiKey = WorkflowDispatchPreflightValidator.ValidateAndResolveApiKey(workflowDefinition);
        }
        catch (WorkflowLoadException ex)
        {
            logger.LogWarning(
                ex,
                "Skipping startup terminal cleanup because workflow preflight validation failed with code {Code} for {WorkflowPath}.",
                ex.Code,
                workflowDefinition.SourcePath);
            return;
        }

        var instanceId = ResolveInstanceId();
        var hasLease = await coordinationStore.AcquireOrRenewLeaseAsync(
            ResolveLeaseName(),
            instanceId,
            ResolveLeaseTtl(),
            cancellationToken);

        if (!hasLease)
        {
            logger.LogDebug(
                "Skipping startup terminal cleanup because lease '{LeaseName}' is owned by another instance. InstanceId={InstanceId}",
                ResolveLeaseName(),
                instanceId);
            return;
        }

        try
        {
            await RunStartupCleanupCoreAsync(workflowDefinition, apiKey, cancellationToken);
        }
        finally
        {
            await coordinationStore.ReleaseLeaseAsync(ResolveLeaseName(), instanceId, CancellationToken.None);
        }
    }

    public async Task<int?> RunTickAsync(CancellationToken cancellationToken)
    {
        var workflowDefinition = await workflowDefinitionProvider.GetCurrentAsync(cancellationToken);
        await PersistWorkflowSnapshotAsync(workflowDefinition, cancellationToken);

        string? apiKey = null;
        WorkflowLoadException? preflightError = null;
        try
        {
            apiKey = WorkflowDispatchPreflightValidator.ValidateAndResolveApiKey(workflowDefinition);
        }
        catch (WorkflowLoadException ex)
        {
            preflightError = ex;
        }

        var instanceId = ResolveInstanceId();
        var hasLease = await coordinationStore.AcquireOrRenewLeaseAsync(
            ResolveLeaseName(),
            instanceId,
            ResolveLeaseTtl(),
            cancellationToken);

        if (!hasLease)
        {
            logger.LogDebug(
                "Skipping tick because lease '{LeaseName}' is owned by another instance. InstanceId={InstanceId}",
                ResolveLeaseName(),
                instanceId);
            return workflowDefinition.Runtime.Polling.IntervalMs;
        }

        try
        {
            await RecoverOrphanedStateAsync(instanceId, workflowDefinition, cancellationToken);
            await ReconcileRunningIssuesAsync(workflowDefinition, apiKey, preflightError, instanceId, cancellationToken);

            if (preflightError is not null || string.IsNullOrWhiteSpace(apiKey))
            {
                logger.LogError(
                    preflightError,
                    "Skipping dispatch for workflow {WorkflowPath} because preflight validation failed with code {Code}.",
                    workflowDefinition.SourcePath,
                    preflightError?.Code ?? "missing_tracker_api_key");
                return workflowDefinition.Runtime.Polling.IntervalMs;
            }

            await DispatchCandidatesAsync(workflowDefinition, apiKey, instanceId, cancellationToken);
            return workflowDefinition.Runtime.Polling.IntervalMs;
        }
        finally
        {
            await coordinationStore.ReleaseLeaseAsync(ResolveLeaseName(), instanceId, CancellationToken.None);
        }
    }

    private string ResolveInstanceId()
    {
        if (!string.IsNullOrWhiteSpace(orchestrationOptions.InstanceId))
        {
            return orchestrationOptions.InstanceId;
        }

        return $"{Environment.MachineName}-{Environment.ProcessId}";
    }

    private string ResolveLeaseName()
    {
        return string.IsNullOrWhiteSpace(orchestrationOptions.LeaseName)
            ? "poll-dispatch"
            : orchestrationOptions.LeaseName;
    }

    private TimeSpan ResolveLeaseTtl() => TimeSpan.FromSeconds(orchestrationOptions.LeaseTtlSeconds);
}
