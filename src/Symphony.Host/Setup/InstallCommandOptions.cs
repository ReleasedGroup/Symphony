namespace Symphony.Host.Setup;

internal sealed record InstallCommandOptions(
    bool NoLaunch,
    bool ShowHelp)
{
    public static InstallCommandOptions Parse(IReadOnlyList<string> args)
    {
        var noLaunch = false;
        var showHelp = false;

        foreach (var arg in args)
        {
            if (arg.Equals("--no-launch", StringComparison.OrdinalIgnoreCase))
            {
                noLaunch = true;
                continue;
            }

            if (arg is "--help" or "-h")
            {
                showHelp = true;
                continue;
            }

            throw new SymphonyCliException($"Unknown install option '{arg}'. Supported options: --no-launch, --help.");
        }

        return new InstallCommandOptions(noLaunch, showHelp);
    }
}
