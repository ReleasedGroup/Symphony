using Symphony.Core.Models;

namespace Symphony.Core.Tests;

public sealed class IssueStateMatcherTests
{
    [Theory]
    [InlineData("Closed")]
    [InlineData("Done")]
    [InlineData("Resolved")]
    [InlineData("Completed")]
    public void IsClosedState_ShouldRecognizeClosedStateSynonyms(string state)
    {
        Assert.True(IssueStateMatcher.IsClosedState(state));
    }

    [Fact]
    public void MatchesConfiguredActiveState_ShouldTreatOpenStateAsActiveWhenNoConfiguredStatesExist()
    {
        Assert.True(IssueStateMatcher.MatchesConfiguredActiveState("Open", []));
    }

    [Fact]
    public void MatchesConfiguredActiveState_ShouldTreatClosedStateAsInactiveWhenNoConfiguredStatesExist()
    {
        Assert.False(IssueStateMatcher.MatchesConfiguredActiveState("Closed", []));
    }

    [Fact]
    public void MatchesConfiguredActiveState_ShouldAllowClosedStateWhenConfiguredStatesContainTerminalSynonym()
    {
        Assert.True(IssueStateMatcher.MatchesConfiguredActiveState("Resolved", ["Open", "Done"]));
    }

    [Fact]
    public void MatchesConfiguredActiveState_ShouldRejectClosedStateWhenConfiguredStatesContainOnlyActiveStates()
    {
        Assert.False(IssueStateMatcher.MatchesConfiguredActiveState("Closed", ["Open", "In Progress"]));
    }

    [Fact]
    public void MatchesConfiguredActiveState_ShouldRequireExactMatchForNonClosedStates()
    {
        Assert.False(IssueStateMatcher.MatchesConfiguredActiveState("Blocked", ["Open", "In Progress"]));
    }

    [Fact]
    public void MatchesConfiguredActiveState_ShouldMatchNonClosedStatesIgnoringCaseAndWhitespace()
    {
        Assert.True(IssueStateMatcher.MatchesConfiguredActiveState("  In Progress ", ["open", "in progress"]));
    }
}
