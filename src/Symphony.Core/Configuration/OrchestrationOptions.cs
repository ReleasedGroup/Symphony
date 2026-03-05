using System.ComponentModel.DataAnnotations;

namespace Symphony.Core.Configuration;

public sealed class OrchestrationOptions
{
    public const string SectionName = "Orchestration";

    public string? InstanceId { get; init; }
    public string LeaseName { get; init; } = "poll-dispatch";

    [Range(30, 86_400)]
    public int LeaseTtlSeconds { get; init; } = 900;
}
