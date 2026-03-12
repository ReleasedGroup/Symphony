namespace Symphony.Host.Setup;

internal sealed record SymphonyInstallationRuntime(
    string BundleRootPath,
    string ExecutableFileName,
    string WindowsSetupScriptFileName,
    string UnixSetupScriptFileName)
{
    public static SymphonyInstallationRuntime CreateDefault()
    {
        return new SymphonyInstallationRuntime(
            Path.GetFullPath(AppContext.BaseDirectory),
            OperatingSystem.IsWindows() ? "Symphony.exe" : "Symphony",
            "setup-symphony.cmd",
            "setup-symphony.sh");
    }
}
