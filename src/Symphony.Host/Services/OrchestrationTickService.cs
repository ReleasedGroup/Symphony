using Microsoft.Extensions.Options;
using Symphony.Core.Abstractions;
using Symphony.Core.Configuration;
using Symphony.Core.Models;
using Symphony.Infrastructure.Tracker.GitHub;
using Symphony.Infrastructure.Workflows;
using Symphony.Infrastructure.Workflows.Models;

namespace Symphony.Host.Services;

public sealed class OrchestrationTickService(
    IWorkflowDefinitionProvider workflowDefinitionProvider,
    IWorkflowPromptRenderer workflowPromptRenderer,
    IGitHubTrackerClient trackerClient,
    IOrchestrationCoordinationStore coordinationStore,
    IWorkspaceManager workspaceManager,
    IWorkspaceHookRunner workspaceHookRunner,
    IAgentRunner agentRunner,
    IOptions<OrchestrationOptions> orchestrationOptions,
    ILogger<OrchestrationTickService> logger)
{
    public async Task<int?> RunTickAsync(CancellationToken cancellationToken)
    {
        var workflowDefinition = await workflowDefinitionProvider.GetCurrentAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(workflowDefinition.Runtime.Hooks.BeforeRemove))
        {
            logger.LogWarning(
                "hooks.before_remove is configured but cleanup execution is not implemented yet.");
        }

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

        var maxConcurrentAgents = workflowDefinition.Runtime.Agent.MaxConcurrentAgents;
        var orderedIssues = OrderIssuesForDispatch(issues).ToList();
        if (orderedIssues.Count > maxConcurrentAgents)
        {
            logger.LogInformation(
                "Found {CandidateCount} ordered candidates; at most {MaxConcurrentAgents} agents can be dispatched this tick.",
                orderedIssues.Count,
                maxConcurrentAgents);
        }

        var dispatchCount = 0;
        foreach (var issue in orderedIssues)
        {
            if (dispatchCount >= maxConcurrentAgents)
            {
                break;
            }

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

            dispatchCount++;

            var releaseStatus = "agent_failed";
            try
            {
                var remoteUrl = ResolveRemoteUrl(
                    workflowDefinition.Runtime.Workspace.RemoteUrl,
                    workflowDefinition.Runtime.Tracker.Owner,
                    workflowDefinition.Runtime.Tracker.Repo);

                var workspace = await workspaceManager.PrepareIssueWorkspaceAsync(
                    new WorkspacePreparationRequest(
                        IssueId: issue.Id,
                        IssueIdentifier: issue.Identifier,
                        SuggestedBranchName: issue.BranchName,
                        WorkspaceRoot: workflowDefinition.Runtime.Workspace.Root,
                        SharedClonePath: workflowDefinition.Runtime.Workspace.SharedClonePath,
                        WorktreesRoot: workflowDefinition.Runtime.Workspace.WorktreesRoot,
                        BaseBranch: workflowDefinition.Runtime.Workspace.BaseBranch,
                        RemoteRepositoryUrl: remoteUrl),
                    cancellationToken);

                logger.LogInformation(
                    "Workspace {WorkspacePath} prepared for issue {IssueIdentifier} ({IssueId}) on branch {BranchName}.",
                    workspace.WorkspacePath,
                    issue.Identifier,
                    issue.Id,
                    workspace.BranchName);

                if (workspace.CreatedNow)
                {
                    await RunRequiredHookAsync(
                        hookName: "after_create",
                        hookScript: workflowDefinition.Runtime.Hooks.AfterCreate,
                        issueIdentifier: issue.Identifier,
                        workspacePath: workspace.WorkspacePath,
                        timeoutMs: workflowDefinition.Runtime.Hooks.TimeoutMs,
                        cancellationToken);
                }

                await RunRequiredHookAsync(
                    hookName: "before_run",
                    hookScript: workflowDefinition.Runtime.Hooks.BeforeRun,
                    issueIdentifier: issue.Identifier,
                    workspacePath: workspace.WorkspacePath,
                    timeoutMs: workflowDefinition.Runtime.Hooks.TimeoutMs,
                    cancellationToken);

                AgentRunResult runResult;
                try
                {
                    var prompt = workflowPromptRenderer.RenderForIssue(workflowDefinition, issue);
                    runResult = await agentRunner.RunIssueAsync(
                        new AgentRunRequest(
                            issue.Id,
                            issue.Identifier,
                            workspace.WorkspacePath,
                            prompt,
                            workflowDefinition.Runtime.Codex.Command,
                            workflowDefinition.Runtime.Codex.TimeoutMs,
                            workflowDefinition.Runtime.Codex.ApprovalPolicy,
                            workflowDefinition.Runtime.Codex.ThreadSandbox,
                            workflowDefinition.Runtime.Codex.TurnSandboxPolicy,
                            workflowDefinition.Runtime.Codex.ReadTimeoutMs),
                        cancellationToken);
                }
                finally
                {
                    await RunBestEffortHookAsync(
                        hookName: "after_run",
                        hookScript: workflowDefinition.Runtime.Hooks.AfterRun,
                        issueIdentifier: issue.Identifier,
                        workspacePath: workspace.WorkspacePath,
                        timeoutMs: workflowDefinition.Runtime.Hooks.TimeoutMs,
                        cancellationToken);
                }

                if (runResult.Success)
                {
                    releaseStatus = "agent_succeeded";
                    logger.LogInformation(
                        "Agent run succeeded for issue {IssueIdentifier} with exit code {ExitCode} in {DurationMs}ms.",
                        issue.Identifier,
                        runResult.ExitCode,
                        (int)runResult.Duration.TotalMilliseconds);
                }
                else
                {
                    releaseStatus = "agent_failed";
                    logger.LogWarning(
                        "Agent run failed for issue {IssueIdentifier} with exit code {ExitCode} in {DurationMs}ms. StdErr: {StdErr}",
                        issue.Identifier,
                        runResult.ExitCode,
                        (int)runResult.Duration.TotalMilliseconds,
                        TruncateForLog(runResult.Stderr, 2_000));
                }
            }
            catch (WorkflowLoadException ex)
            {
                releaseStatus = "prompt_failed";
                logger.LogError(
                    ex,
                    "Failed rendering prompt for issue {IssueIdentifier} ({IssueId}) with code {Code}.",
                    issue.Identifier,
                    issue.Id,
                    ex.Code);
            }
            catch (WorkspaceHookExecutionException ex) when (string.Equals(ex.HookName, "after_create", StringComparison.OrdinalIgnoreCase))
            {
                releaseStatus = "after_create_failed";
                logger.LogError(
                    ex,
                    "after_create hook failed for issue {IssueIdentifier} ({IssueId}).",
                    issue.Identifier,
                    issue.Id);
            }
            catch (WorkspaceHookExecutionException ex) when (string.Equals(ex.HookName, "before_run", StringComparison.OrdinalIgnoreCase))
            {
                releaseStatus = "before_run_failed";
                logger.LogError(
                    ex,
                    "before_run hook failed for issue {IssueIdentifier} ({IssueId}).",
                    issue.Identifier,
                    issue.Id);
            }
            catch (Exception ex)
            {
                releaseStatus = "dispatch_failed";
                logger.LogError(ex, "Failed processing issue {IssueIdentifier} ({IssueId}).", issue.Identifier, issue.Id);
            }
            finally
            {
                await coordinationStore.ReleaseIssueClaimAsync(
                    issue.Id,
                    instanceId,
                    releaseStatus,
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

    private static string ResolveRemoteUrl(string? configuredRemoteUrl, string owner, string repo)
    {
        if (!string.IsNullOrWhiteSpace(configuredRemoteUrl))
        {
            return configuredRemoteUrl;
        }

        return $"https://github.com/{owner}/{repo}.git";
    }

    private static string TruncateForLog(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (value.Length <= maxLength)
        {
            return value;
        }

        return $"{value[..maxLength]}...";
    }

    private async Task RunRequiredHookAsync(
        string hookName,
        string? hookScript,
        string issueIdentifier,
        string workspacePath,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hookScript))
        {
            return;
        }

        await workspaceHookRunner.RunHookAsync(
            new WorkspaceHookRequest(
                HookName: hookName,
                Script: hookScript,
                WorkspacePath: workspacePath,
                TimeoutMs: timeoutMs,
                IssueIdentifier: issueIdentifier),
            cancellationToken);
    }

    private async Task RunBestEffortHookAsync(
        string hookName,
        string? hookScript,
        string issueIdentifier,
        string workspacePath,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(hookScript))
        {
            return;
        }

        try
        {
            await workspaceHookRunner.RunHookAsync(
                new WorkspaceHookRequest(
                    HookName: hookName,
                    Script: hookScript,
                    WorkspacePath: workspacePath,
                    TimeoutMs: timeoutMs,
                    IssueIdentifier: issueIdentifier),
                cancellationToken);
        }
        catch (WorkspaceHookExecutionException ex)
        {
            logger.LogWarning(
                ex,
                "{HookName} hook failed for issue {IssueIdentifier}. Ignoring by design.",
                hookName,
                issueIdentifier);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "{HookName} hook hit an unexpected error for issue {IssueIdentifier}. Ignoring by design.",
                hookName,
                issueIdentifier);
        }
    }

    private static IEnumerable<NormalizedIssue> OrderIssuesForDispatch(IEnumerable<NormalizedIssue> issues)
    {
        return issues
            .OrderBy(issue => issue.Priority.HasValue ? 0 : 1)
            .ThenBy(issue => issue.Priority ?? int.MaxValue)
            .ThenBy(issue => issue.CreatedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(issue => issue.Identifier, StringComparer.OrdinalIgnoreCase)
            .ThenBy(issue => issue.Id, StringComparer.OrdinalIgnoreCase);
    }
}
