using Symphony.Core.Models;

namespace Symphony.Core.Tests;

public sealed class IssueStateSnapshotTests
{
    [Theory]
    [InlineData("Open", true, true)]
    [InlineData("Open", false, false)]
    [InlineData("Closed", true, false)]
    [InlineData("Closed", false, false)]
    public void ExecutionEligibilityRequiresActiveStateAndCandidateFilters(string state, bool matchesFilters, bool expected)
    {
        Assert.Equal(expected, new IssueStateSnapshot("issue-1", state, matchesFilters).IsExecutionEligible(["Open"]));
    }
}
