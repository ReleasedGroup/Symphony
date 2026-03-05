namespace Symphony.Core.Configuration;

public sealed class SymphonyRuntimeOptions
{
    public const string SectionName = "Symphony";

    public TrackerOptions Tracker { get; init; } = new();
    public PollingOptions Polling { get; init; } = new();
    public AgentOptions Agent { get; init; } = new();
}
