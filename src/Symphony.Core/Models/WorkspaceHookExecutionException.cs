namespace Symphony.Core.Models;

public sealed class WorkspaceHookExecutionException : Exception
{
    public WorkspaceHookExecutionException(
        string hookName,
        string message,
        bool isTimeout = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        HookName = hookName;
        IsTimeout = isTimeout;
    }

    public string HookName { get; }

    public bool IsTimeout { get; }
}
