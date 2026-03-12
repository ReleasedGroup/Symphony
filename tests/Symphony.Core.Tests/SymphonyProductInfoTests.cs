using Symphony.Core.Metadata;

namespace Symphony.Core.Tests;

public sealed class SymphonyProductInfoTests
{
    [Fact]
    public void ProductMetadata_ShouldExposeStableApplicationIdentity()
    {
        Assert.Equal("Symphony", SymphonyProductInfo.Name);
        Assert.False(string.IsNullOrWhiteSpace(SymphonyProductInfo.DisplayVersion));
        Assert.False(string.IsNullOrWhiteSpace(SymphonyProductInfo.ProtocolVersion));
        Assert.False(string.IsNullOrWhiteSpace(SymphonyProductInfo.UserAgentVersion));
    }

    [Fact]
    public void TokenVersions_ShouldNotContainBuildMetadataSeparators()
    {
        Assert.DoesNotContain('+', SymphonyProductInfo.ProtocolVersion);
        Assert.DoesNotContain('+', SymphonyProductInfo.UserAgentVersion);
    }
}
