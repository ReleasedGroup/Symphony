namespace Symphony.Core.Models;

public sealed record AgentRunResult(
    bool Success,
    int ExitCode,
    string Stdout,
    string Stderr,
    TimeSpan Duration);
