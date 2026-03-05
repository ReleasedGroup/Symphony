using Microsoft.Extensions.Options;
using Symphony.Core.Abstractions;
using Symphony.Core.Configuration;
using Symphony.Core.Models;
using Symphony.Infrastructure.Tracker.GitHub;
using Symphony.Infrastructure.Workflows;

namespace Symphony.Host.Services;

public sealed class OrchestrationTickService(
    IWorkflowDefinitionProvider workflowDefinitionProvider,
    IGitHubTrackerClient trackerClient,
    IOrchestrationCoordinationStore coordinationStore,
    IOptions<OrchestrationOptions> orchestrationOptions,
    ILogger<OrchestrationTickService> logger)
{
    public async Task<int?> RunTickAsync(CancellationToken cancellationToken)
    {
        var workflowDefinition = await workflowDefinitionProvider.GetCurrentAsync(cancellationToken);
        var instanceId = ResolveInstanceId(orchestrationOptions.Value);
        var leaseName = string.IsNullOrWhiteSpace(orchestrationOptions.Value.LeaseName)
            ? "poll-dispatch"
            : orchestrationOptions.Value.LeaseName;
        var leaseTtl = TimeSpan.FromSeconds(orchestrationOptions.Value.LeaseTtlSeconds);

        var hasLease = await coordinationStore.AcquireOrRenewLeaseAsync(
            leaseName,
            instanceId,
            leaseTtl,
            cancellationToken);

        if (!hasLease)
        {
            logger.LogDebug(
                "Skipping tick because lease '{LeaseName}' is owned by another instance. InstanceId={InstanceId}",
                leaseName,
                instanceId);
            return workflowDefinition.Runtime.Polling.IntervalMs;
        }

        var apiKey = ResolveApiKey(workflowDefinition.Runtime.Tracker.ApiKey);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning(
                "Skipping tracker poll. tracker.api_key is missing after environment resolution for workflow {WorkflowPath}.",
                workflowDefinition.SourcePath);
            return workflowDefinition.Runtime.Polling.IntervalMs;
        }

        var query = new TrackerQuery(
            workflowDefinition.Runtime.Tracker.Endpoint,
            apiKey,
            workflowDefinition.Runtime.Tracker.Owner,
            workflowDefinition.Runtime.Tracker.Repo,
            workflowDefinition.Runtime.Tracker.ActiveStates,
            workflowDefinition.Runtime.Tracker.Labels,
            workflowDefinition.Runtime.Tracker.Milestone);

        var issues = await trackerClient.FetchCandidateIssuesAsync(query, cancellationToken);
        logger.LogInformation(
            "Fetched {IssueCount} candidate issues from {Owner}/{Repo}.",
            issues.Count,
            query.Owner,
            query.Repo);

        foreach (var issue in issues)
        {
            var claimed = await coordinationStore.TryClaimIssueAsync(
                issue.Id,
                issue.Identifier,
                instanceId,
                cancellationToken);

            if (!claimed)
            {
                logger.LogDebug("Issue {IssueIdentifier} already claimed by another instance.", issue.Identifier);
                continue;
            }

            try
            {
                // Dispatch hook placeholder until agent execution is wired.
                logger.LogInformation(
                    "Claimed issue {IssueIdentifier} ({IssueId}) for dispatch placeholder.",
                    issue.Identifier,
                    issue.Id);
            }
            finally
            {
                await coordinationStore.ReleaseIssueClaimAsync(
                    issue.Id,
                    instanceId,
                    "released",
                    cancellationToken);
            }
        }

        return workflowDefinition.Runtime.Polling.IntervalMs;
    }

    private static string ResolveInstanceId(OrchestrationOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.InstanceId))
        {
            return options.InstanceId;
        }

        return $"{Environment.MachineName}-{Environment.ProcessId}";
    }

    private static string? ResolveApiKey(string rawApiKey)
    {
        if (string.IsNullOrWhiteSpace(rawApiKey))
        {
            return null;
        }

        if (!rawApiKey.StartsWith('$'))
        {
            return rawApiKey;
        }

        var variableName = rawApiKey[1..].Trim();
        if (string.IsNullOrWhiteSpace(variableName))
        {
            return null;
        }

        return Environment.GetEnvironmentVariable(variableName);
    }
}
