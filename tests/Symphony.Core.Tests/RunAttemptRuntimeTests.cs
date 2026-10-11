using Symphony.Core.Models;

namespace Symphony.Core.Tests;

public sealed class RunAttemptRuntimeTests
{
    [Theory]
    [Trait("Spec", "17.6")]
    [InlineData(RunStatusNames.Succeeded, 30, 60, 30)]
    [InlineData(RunStatusNames.Failed, 30, 60, 30)]
    [InlineData(RunStatusNames.Running, null, 60, 60)]
    [InlineData(RunStatusNames.Failed, null, 60, 0)]
    [InlineData(RunStatusNames.Succeeded, -30, 60, 0)]
    [InlineData(RunStatusNames.Running, null, -60, 0)]
    public void RuntimeAccounting_ShouldIncludeCompletedAndActiveTimeWithoutNegativeDurations(
        string status, int? completedSeconds, int snapshotSeconds, double expected)
    {
        var start = DateTimeOffset.UnixEpoch;
        Assert.Equal(expected, RunAttemptRuntime.CalculateSeconds(start,
            completedSeconds.HasValue ? start.AddSeconds(completedSeconds.Value) : null,
            status, start.AddSeconds(snapshotSeconds)));
    }
}
