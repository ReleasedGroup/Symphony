namespace Symphony.Infrastructure.Agent.Codex;

internal static class CodexProtocolValueNormalizer
{
    public static string NormalizeThreadSandbox(string sandbox)
    {
        var value = sandbox.Trim();
        return NormalizeSandboxKey(value) switch
        {
            "readonly" => "read-only",
            "workspacewrite" => "workspace-write",
            "dangerfullaccess" => "danger-full-access",
            _ => value
        };
    }

    public static string GetTurnSandboxPolicyType(string sandboxPolicy)
    {
        var value = sandboxPolicy.Trim();
        return NormalizeSandboxKey(value) switch
        {
            "readonly" => "readOnly",
            "workspacewrite" => "workspaceWrite",
            "dangerfullaccess" => "dangerFullAccess",
            "externalsandbox" => "externalSandbox",
            _ => value
        };
    }

    private static string NormalizeSandboxKey(string value)
    {
        return string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }
}
