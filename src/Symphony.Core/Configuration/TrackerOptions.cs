namespace Symphony.Core.Configuration;

public sealed class TrackerOptions
{
    public string Kind { get; init; } = "github";
    public string Endpoint { get; init; } = "https://api.github.com/graphql";
    public string ApiKey { get; init; } = "$GITHUB_TOKEN";
    public string Owner { get; init; } = string.Empty;
    public string Repo { get; init; } = string.Empty;
    public string? Milestone { get; init; }
    public List<string> Labels { get; init; } = [];
    public List<string> ActiveStates { get; init; } = ["Open", "In Progress"];
    public List<string> TerminalStates { get; init; } = ["Closed"];
}
