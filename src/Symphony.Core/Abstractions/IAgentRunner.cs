using Symphony.Core.Models;

namespace Symphony.Core.Abstractions;

public interface IAgentRunner
{
    Task<AgentRunResult> RunIssueAsync(
        AgentRunRequest request,
        Func<AgentRunUpdate, CancellationToken, Task>? onUpdate = null,
        CancellationToken cancellationToken = default);
}
