using Zexoro.Versioning;

namespace Zexoro.Formats.MacOS.Tests;

public sealed class BundleVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3-beta.1", "1.2.3")]
    [InlineData("10.0.0+build.7", "10.0.0")]
    [InlineData("0.1.0-rc.1+sha.abc", "0.1.0")]
    public void ShortVersion_IsMajorMinorPatch(string version, string expected)
    {
        Assert.Equal(expected, BundleVersion.ShortVersion(SemanticVersion.Parse(version)));
        Assert.Equal(expected, BundleVersion.BuildVersion(SemanticVersion.Parse(version)));
    }
}
