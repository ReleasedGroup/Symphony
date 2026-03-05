using System.ComponentModel.DataAnnotations;
using Symphony.Core.Defaults;

namespace Symphony.Core.Configuration;

public sealed class AgentOptions
{
    [Range(1, 500)]
    public int MaxConcurrentAgents { get; init; } = SymphonyDefaults.MaxConcurrentAgents;
}
