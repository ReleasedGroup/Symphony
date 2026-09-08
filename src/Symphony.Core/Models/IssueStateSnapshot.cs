namespace Symphony.Core.Models;

public sealed record IssueStateSnapshot(
    string Id,
    string State,
    bool MatchesCandidateFilters = true)
{
    public bool IsExecutionEligible(IReadOnlyList<string> activeStates) =>
        MatchesCandidateFilters && IssueStateMatcher.MatchesConfiguredActiveState(State, activeStates);
}
