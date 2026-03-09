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
        var normalized = state.Trim().ToLowerInvariant();
        return normalized is "closed" or "done" or "resolved" or "completed";
    }

    /// <summary>
    /// Returns <see langword="true"/> when the issue state should be treated as active for the configured state set.
    /// </summary>
    public static bool MatchesConfiguredActiveState(string issueState, IReadOnlyList<string> configuredStates)
    {
        if (configuredStates.Count == 0)
        {
            return !IsClosedState(issueState);
        }

        if (IsClosedState(issueState))
        {
            return configuredStates.Any(IsClosedState);
        }

        return configuredStates.Any(state => !IsClosedState(state));
    }
}
