namespace Symphony.Core.Models;

public static class RunAttemptRuntime
{
    public static double CalculateSeconds(
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string status,
        DateTimeOffset snapshotAtUtc)
    {
        var end = completedAtUtc ?? (status == RunStatusNames.Running ? snapshotAtUtc : (DateTimeOffset?)null);
        return end.HasValue ? Math.Max((end.Value - startedAtUtc).TotalSeconds, 0d) : 0d;
    }
}
