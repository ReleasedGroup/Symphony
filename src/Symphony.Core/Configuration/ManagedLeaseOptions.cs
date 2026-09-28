using System.ComponentModel.DataAnnotations;

namespace Symphony.Core.Configuration;

public sealed class ManagedLeaseOptions
{
    public const string SectionName = "ManagedLease";

    public bool Enabled { get; init; }
    public string InstanceId { get; init; } = string.Empty;
    public string GenerationId { get; init; } = string.Empty;
    public string SigningKeyReference { get; init; } = string.Empty;

    [Range(1, 300)]
    public int MaxLeaseSeconds { get; init; } = 60;

    [Range(0, 30)]
    public int MaxClockSkewSeconds { get; init; } = 5;
}
