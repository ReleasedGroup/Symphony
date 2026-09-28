using System.Security.Cryptography;
using System.Text;

namespace Symphony.Infrastructure.Workflows;

public static class WorkflowRevision
{
    public static string Compute(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
