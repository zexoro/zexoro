using Zexoro.Versioning;

namespace Zexoro.Core.Tests;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("0.0.0", 0UL, 0UL, 0UL, "", "")]
    [InlineData("1.2.3", 1UL, 2UL, 3UL, "", "")]
    [InlineData("1.0.0-alpha", 1UL, 0UL, 0UL, "alpha", "")]
    [InlineData("1.0.0-alpha.1", 1UL, 0UL, 0UL, "alpha.1", "")]
    [InlineData("1.0.0-0.3.7", 1UL, 0UL, 0UL, "0.3.7", "")]
    [InlineData("1.0.0-x.7.z.92", 1UL, 0UL, 0UL, "x.7.z.92", "")]
    [InlineData("1.0.0-x-y-z.--", 1UL, 0UL, 0UL, "x-y-z.--", "")]
    [InlineData("1.0.0-alpha+001", 1UL, 0UL, 0UL, "alpha", "001")]
    [InlineData("1.0.0+20130313144700", 1UL, 0UL, 0UL, "", "20130313144700")]
    [InlineData("1.0.0-beta+exp.sha.5114f85", 1UL, 0UL, 0UL, "beta", "exp.sha.5114f85")]
    [InlineData("1.0.0+21AF26D3----117B344092BD", 1UL, 0UL, 0UL, "", "21AF26D3----117B344092BD")]
    [InlineData("1.0.0-rc.1+build-1.2", 1UL, 0UL, 0UL, "rc.1", "build-1.2")]
    [InlineData("18446744073709551615.0.0", ulong.MaxValue, 0UL, 0UL, "", "")]
    public void Parse_ValidVersion_ReturnsComponents(string text, ulong major, ulong minor, ulong patch, string prerelease, string build)
    {
        var version = SemanticVersion.Parse(text);

        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(prerelease, string.Join('.', version.Prerelease));
        Assert.Equal(build, string.Join('.', version.Build));
        Assert.Equal(prerelease.Length > 0, version.IsPrerelease);
        Assert.Equal(text, version.ToString());
    }

    [Theory]
    [InlineData("", "expected MAJOR.MINOR.PATCH")]
    [InlineData("1", "expected MAJOR.MINOR.PATCH")]
    [InlineData("1.2", "expected MAJOR.MINOR.PATCH")]
    [InlineData("1.2.3.4", "expected MAJOR.MINOR.PATCH")]
    [InlineData("v1.2.3", "major version must be a non-negative integer")]
    [InlineData(" 1.2.3", "major version must be a non-negative integer")]
    [InlineData("1.2.3 ", "patch version must be a non-negative integer")]
    [InlineData("-1.2.3", "expected MAJOR.MINOR.PATCH")]
    [InlineData("1..3", "minor version must be a non-negative integer")]
    [InlineData("01.2.3", "major version must not have leading zeros")]
    [InlineData("1.02.3", "minor version must not have leading zeros")]
    [InlineData("1.2.03", "patch version must not have leading zeros")]
    [InlineData("18446744073709551616.0.0", "major version is too large")]
    [InlineData("1.2.3-", "pre-release identifiers must not be empty")]
    [InlineData("1.2.3-alpha..1", "pre-release identifiers must not be empty")]
    [InlineData("1.2.3-01", "numeric pre-release identifiers must not have leading zeros")]
    [InlineData("1.2.3-alpha_1", "pre-release identifiers may contain only")]
    [InlineData("1.2.3+", "build metadata identifiers must not be empty")]
    [InlineData("1.2.3+a+b", "build metadata identifiers may contain only")]
    [InlineData("1.2.3+ä", "build metadata identifiers may contain only")]
    [InlineData("1.2.3-é", "pre-release identifiers may contain only")]
    public void Parse_InvalidVersion_ThrowsWithReason(string text, string reason)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
        var ex = Assert.Throws<FormatException>(() => SemanticVersion.Parse(text));
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_Null_ReturnsFalse()
    {
        Assert.False(SemanticVersion.TryParse(null, out var version));
        Assert.Null(version);
    }

    [Fact]
    public void CompareTo_SpecificationExamples_AreInIncreasingPrecedence()
    {
        // Semantic Versioning 2.0.0, section 11.
        string[] ordered =
        [
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2",
            "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "2.0.0", "2.1.0", "2.1.1",
        ];

        for (var i = 0; i < ordered.Length; i++)
        {
            for (var j = 0; j < ordered.Length; j++)
            {
                var left = SemanticVersion.Parse(ordered[i]);
                var right = SemanticVersion.Parse(ordered[j]);
                Assert.Equal(i.CompareTo(j), Math.Sign(left.CompareTo(right)));
            }
        }
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("9.0.0", "10.0.0")]
    [InlineData("1.0.0-2", "1.0.0-10")]
    [InlineData("1.0.0-999", "1.0.0-a")]
    [InlineData("1.0.0-A", "1.0.0-a")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.0")]
    [InlineData("1.0.0-99999999999999999999998", "1.0.0-99999999999999999999999")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    public void Operators_OrderByPrecedence(string lower, string higher)
    {
        var a = SemanticVersion.Parse(lower);
        var b = SemanticVersion.Parse(higher);

        Assert.True(a < b);
        Assert.True(a <= b);
        Assert.True(b > a);
        Assert.True(b >= a);
        Assert.False(a > b);
        Assert.True(a != b);
    }

    [Fact]
    public void CompareTo_IgnoresBuildMetadata_ButEqualityDoesNot()
    {
        var a = SemanticVersion.Parse("1.0.0+a");
        var b = SemanticVersion.Parse("1.0.0+b");

        Assert.Equal(0, a.CompareTo(b));
        Assert.True(a <= b);
        Assert.True(a >= b);
        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    [Fact]
    public void Equals_SameText_IsEqualWithSameHashCode()
    {
        var a = SemanticVersion.Parse("1.2.3-rc.1+build.7");
        var b = SemanticVersion.Parse("1.2.3-rc.1+build.7");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(0, a.CompareTo(b));
    }

    [Fact]
    public void CompareTo_Null_IsGreater()
    {
        var version = SemanticVersion.Parse("0.0.0");

        Assert.True(version.CompareTo(null) > 0);
        Assert.True(version > null);
        Assert.True(null < version);
        Assert.False(version == null);
    }

    [Fact]
    public void Sort_UsesPrecedence()
    {
        string[] unsorted = ["1.0.0", "1.0.0-beta", "0.9.9", "1.0.0-alpha"];
        string[] sorted = ["0.9.9", "1.0.0-alpha", "1.0.0-beta", "1.0.0"];
        var versions = unsorted.Select(SemanticVersion.Parse).ToList();

        versions.Sort();

        Assert.Equal(sorted, versions.Select(v => v.ToString()));
    }
}
