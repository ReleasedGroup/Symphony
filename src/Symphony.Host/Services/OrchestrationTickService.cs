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
    public async Task RunStartupCleanupAsync(CancellationToken cancellationToken)
    {
        var workflowDefinition = await workflowDefinitionProvider.GetCurrentAsync(cancellationToken);
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
                "Skipping startup terminal cleanup because lease '{LeaseName}' is owned by another instance. InstanceId={InstanceId}",
                leaseName,
                instanceId);
            return;
        }

        try
        {
            var terminalStates = workflowDefinition.Runtime.Tracker.TerminalStates;
            if (terminalStates.Count == 0)
            {
                logger.LogDebug("Skipping startup terminal cleanup because tracker.terminal_states is empty.");
                return;
            }

            var query = new TrackerQuery(
                workflowDefinition.Runtime.Tracker.Endpoint,
                apiKey,
                workflowDefinition.Runtime.Tracker.Owner,
                workflowDefinition.Runtime.Tracker.Repo,
                ActiveStates: terminalStates,
                Labels: [],
                Milestone: null,
                IncludePullRequests: workflowDefinition.Runtime.Tracker.IncludePullRequests);

            IReadOnlyList<NormalizedIssue> terminalIssues;
            try
            {
                terminalIssues = await trackerClient.FetchIssuesByStatesAsync(query, terminalStates, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Startup terminal cleanup could not fetch terminal issues for {Owner}/{Repo}. Continuing startup.",
                    query.Owner,
                    query.Repo);
                return;
            }

            logger.LogInformation(
                "Startup terminal cleanup fetched {IssueCount} terminal issues from {Owner}/{Repo}.",
                terminalIssues.Count,
                query.Owner,
                query.Repo);

            foreach (var issue in terminalIssues)
            {
                try
                {
                    var cleanupResult = await workspaceManager.CleanupIssueWorkspaceAsync(
                        new WorkspaceCleanupRequest(
                            IssueIdentifier: issue.Identifier,
                            WorkspaceRoot: workflowDefinition.Runtime.Workspace.Root,
                            SharedClonePath: workflowDefinition.Runtime.Workspace.SharedClonePath,
                            WorktreesRoot: workflowDefinition.Runtime.Workspace.WorktreesRoot,
                            BeforeRemoveHook: workflowDefinition.Runtime.Hooks.BeforeRemove,
                            HookTimeoutMs: workflowDefinition.Runtime.Hooks.TimeoutMs),
                        cancellationToken);

                    if (cleanupResult.RemovedNow)
                    {
                        logger.LogInformation(
                            "Startup terminal cleanup removed workspace {WorkspacePath} for issue {IssueIdentifier}.",
                            cleanupResult.WorkspacePath,
                            issue.Identifier);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Startup terminal cleanup failed for issue {IssueIdentifier}. Continuing startup.",
                        issue.Identifier);
                }
            }
        }
        finally
        {
            await coordinationStore.ReleaseLeaseAsync(leaseName, instanceId, CancellationToken.None);
        }
    }

    public async Task<int?> RunTickAsync(CancellationToken cancellationToken)
    {
        var workflowDefinition = await workflowDefinitionProvider.GetCurrentAsync(cancellationToken);
        var pollIntervalMs = workflowDefinition.Runtime.Polling.IntervalMs;
        string apiKey;
        try
        {
            apiKey = WorkflowDispatchPreflightValidator.ValidateAndResolveApiKey(workflowDefinition);
        }
        catch (WorkflowLoadException ex)
        {
            logger.LogError(
                ex,
                "Skipping dispatch for workflow {WorkflowPath} because preflight validation failed with code {Code}.",
                workflowDefinition.SourcePath,
                ex.Code);
            return pollIntervalMs;
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
            return pollIntervalMs;
        }

        var query = new TrackerQuery(
            workflowDefinition.Runtime.Tracker.Endpoint,
            apiKey,
            workflowDefinition.Runtime.Tracker.Owner,
            workflowDefinition.Runtime.Tracker.Repo,
            workflowDefinition.Runtime.Tracker.ActiveStates,
            workflowDefinition.Runtime.Tracker.Labels,
            workflowDefinition.Runtime.Tracker.Milestone,
            workflowDefinition.Runtime.Tracker.IncludePullRequests);

        var issues = await trackerClient.FetchCandidateIssuesAsync(query, cancellationToken);
        logger.LogInformation(
            "Fetched {IssueCount} candidate issues from {Owner}/{Repo}.",
            issues.Count,
            query.Owner,
            query.Repo);

        var reconciledIssues = await ReconcileCandidateStatesAsync(
            query,
            workflowDefinition.Runtime.Tracker.ActiveStates,
            issues,
            cancellationToken);

        var maxConcurrentAgents = workflowDefinition.Runtime.Agent.MaxConcurrentAgents;
        var orderedIssues = OrderIssuesForDispatch(reconciledIssues).ToList();
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
                            workflowDefinition.Runtime.Codex.TurnTimeoutMs,
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

        return pollIntervalMs;
    }

    private static string ResolveInstanceId(OrchestrationOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.InstanceId))
        {
            return options.InstanceId;
        }

        return $"{Environment.MachineName}-{Environment.ProcessId}";
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

    private async Task<IReadOnlyList<NormalizedIssue>> ReconcileCandidateStatesAsync(
        TrackerQuery query,
        IReadOnlyList<string> activeStates,
        IReadOnlyList<NormalizedIssue> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        try
        {
            var refreshedStates = await trackerClient.FetchIssueStatesByIdsAsync(
                query,
                candidates.Select(issue => issue.Id).ToList(),
                cancellationToken);

            if (refreshedStates.Count == 0)
            {
                return candidates;
            }

            var refreshedStatesById = refreshedStates
                .Where(state => !string.IsNullOrWhiteSpace(state.Id))
                .GroupBy(state => state.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

            var filtered = new List<NormalizedIssue>(candidates.Count);
            foreach (var candidate in candidates)
            {
                if (!refreshedStatesById.TryGetValue(candidate.Id, out var refreshedState))
                {
                    filtered.Add(candidate);
                    continue;
                }

                if (!IssueStateMatcher.MatchesConfiguredActiveState(refreshedState.State, activeStates))
                {
                    logger.LogDebug(
                        "Skipping candidate issue {IssueIdentifier} ({IssueId}) because refreshed state is {RefreshedState}.",
                        candidate.Identifier,
                        candidate.Id,
                        refreshedState.State);
                    continue;
                }

                filtered.Add(
                    candidate.State.Equals(refreshedState.State, StringComparison.OrdinalIgnoreCase)
                        ? candidate
                        : candidate with { State = refreshedState.State });
            }

            if (filtered.Count != candidates.Count)
            {
                logger.LogInformation(
                    "Filtered {FilteredCount} candidate issue(s) after state reconciliation.",
                    candidates.Count - filtered.Count);
            }

            return filtered;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "State reconciliation failed for {Owner}/{Repo}. Continuing with candidate snapshot states.",
                query.Owner,
                query.Repo);
            return candidates;
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
