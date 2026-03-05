using Symphony.Core.Configuration;
using Symphony.Core.Defaults;

namespace Symphony.Core.Tests;

public sealed class RuntimeDefaultsTests
{
    [Fact]
    public void RuntimeOptions_ShouldUseLockedDefaultValues()
    {
        var options = new SymphonyRuntimeOptions();

        Assert.Equal("github", options.Tracker.Kind);
        Assert.Equal(SymphonyDefaults.MaxConcurrentAgents, options.Agent.MaxConcurrentAgents);
        Assert.Equal(SymphonyDefaults.PollIntervalMs, options.Polling.IntervalMs);
        Assert.Contains("Closed", options.Tracker.TerminalStates);
    }
}
