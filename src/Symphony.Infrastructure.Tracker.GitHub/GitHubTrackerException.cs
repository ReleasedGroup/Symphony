namespace Symphony.Infrastructure.Tracker.GitHub;

public sealed class GitHubTrackerException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
