namespace Symphony.Core.Models;

public static class WorkerLeaseNames
{
    public const string Prefix = "worker:";

    public static string ForRun(string runId) => $"{Prefix}{runId}:";

    public static string ForAttempt(string runId, string attemptId) => $"{ForRun(runId)}{attemptId}";
}
