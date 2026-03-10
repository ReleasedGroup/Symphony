namespace Symphony.Core.Models;

/// <summary>
/// Provides shared state-name matching semantics for tracker-backed issue state comparisons.
/// </summary>
public static class IssueStateMatcher
{
    /// <summary>
    /// Returns <see langword="true"/> when the supplied state should be treated as terminal/closed.
    /// </summary>
    public static bool IsClosedState(string state)
    {
        var normalized = NormalizeState(state);
        return normalized is "closed" or "done" or "resolved" or "completed";
    }

    /// <summary>
    /// Returns <see langword="true"/> when the issue state should be treated as active for the configured state set.
    /// </summary>
    public static bool MatchesConfiguredActiveState(string issueState, IReadOnlyList<string> configuredStates)
    {
        var normalizedIssueState = NormalizeState(issueState);
        if (string.IsNullOrWhiteSpace(normalizedIssueState))
        {
            return false;
        }

        if (configuredStates.Count == 0)
        {
            return !IsClosedState(normalizedIssueState);
        }

        if (IsClosedState(normalizedIssueState))
        {
            return configuredStates.Any(IsClosedState);
        }

        return configuredStates.Any(state =>
            !IsClosedState(state) &&
            string.Equals(NormalizeState(state), normalizedIssueState, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeState(string state) => state.Trim().ToLowerInvariant();
}
