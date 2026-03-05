using Symphony.Core.Models;

namespace Symphony.Core.Abstractions;

public interface IAgentRunner
{
    Task<AgentRunResult> RunIssueAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default);
}
