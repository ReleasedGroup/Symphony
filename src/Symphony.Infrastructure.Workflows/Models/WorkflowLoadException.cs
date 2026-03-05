namespace Symphony.Infrastructure.Workflows.Models;

public sealed class WorkflowLoadException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
