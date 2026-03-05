namespace Symphony.Infrastructure.Agent.Codex;

public sealed class CodexAgentRunner
{
    public Task<string> StartSessionAsync(string issueIdentifier, CancellationToken cancellationToken = default)
    {
        return Task.FromResult($"stub-session-for-{issueIdentifier}");
    }
}
