namespace Symphony.Core.Models;

public sealed record AgentRunUpdate(
    string EventType,
    DateTimeOffset Timestamp,
    string? ThreadId = null,
    string? TurnId = null,
    int? CodexAppServerPid = null,
    string? Message = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    int? TotalTokens = null)
{
    public string? SessionId =>
        string.IsNullOrWhiteSpace(ThreadId) || string.IsNullOrWhiteSpace(TurnId)
            ? null
            : $"{ThreadId}-{TurnId}";
}
