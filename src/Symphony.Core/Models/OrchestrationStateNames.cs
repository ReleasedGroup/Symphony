namespace Symphony.Core.Models;

public static class RunStatusNames
{
    public const string Running = "running";
    public const string Retrying = "retrying";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string TimedOut = "timed_out";
    public const string Stalled = "stalled";
    public const string CanceledByReconciliation = "canceled_by_reconciliation";
    public const string ReleasedIneligible = "released_ineligible";
}

public static class RetryDelayTypes
{
    public const string Continuation = "continuation";
    public const string Backoff = "backoff";
}

public static class RunStopReasons
{
    public const string Terminal = "terminal";
    public const string Inactive = "inactive";
    public const string Stalled = "stalled";
    public const string Shutdown = "shutdown";
}
