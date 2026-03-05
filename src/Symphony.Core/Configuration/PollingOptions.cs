using System.ComponentModel.DataAnnotations;
using Symphony.Core.Defaults;

namespace Symphony.Core.Configuration;

public sealed class PollingOptions
{
    [Range(1_000, 86_400_000)]
    public int IntervalMs { get; init; } = SymphonyDefaults.PollIntervalMs;
}
