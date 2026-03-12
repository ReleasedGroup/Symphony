using System.Reflection;
using System.Text;

namespace Symphony.Core.Metadata;

public static class SymphonyProductInfo
{
    public const string Name = "Symphony";
    private static readonly Lazy<ProductVersionInfo> VersionInfo = new(ResolveVersionInfo);

    public static string DisplayVersion => VersionInfo.Value.DisplayVersion;

    public static string ProtocolVersion => VersionInfo.Value.ProtocolVersion;

    public static string UserAgentVersion => VersionInfo.Value.UserAgentVersion;

    private static ProductVersionInfo ResolveVersionInfo()
    {
        var assembly = typeof(SymphonyProductInfo).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        var displayVersion = string.IsNullOrWhiteSpace(informationalVersion)
            ? assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            : informationalVersion.Trim();

        var tokenVersion = ToTokenVersion(displayVersion);
        return new ProductVersionInfo(displayVersion, tokenVersion, tokenVersion);
    }

    private static string ToTokenVersion(string version)
    {
        var sanitized = version.Split('+', 2, StringSplitOptions.TrimEntries)[0];
        var builder = new StringBuilder(sanitized.Length);

        foreach (var character in sanitized)
        {
            if (char.IsLetterOrDigit(character) || character is '.' or '-')
            {
                builder.Append(character);
            }
        }

        return builder.Length == 0 ? "0.0.0" : builder.ToString();
    }

    private readonly record struct ProductVersionInfo(
        string DisplayVersion,
        string ProtocolVersion,
        string UserAgentVersion);
}
