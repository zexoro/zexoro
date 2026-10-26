using Zexoro.Versioning;

namespace Zexoro.Formats.Nsis.Tests;

public sealed class NsisVersionOrderTests
{
    /// <summary>Ascending precedence, from the Semantic Versioning specification plus edge cases.</summary>
    private static readonly string[] Ascending =
    [
        "0.0.0-0",
        "0.0.0-9",
        "0.0.0-10",
        "0.0.0-a",
        "0.0.0",
        "0.0.1",
        "0.9.65535",
        "1.0.0-0.3.7",
        "1.0.0-alpha",
        "1.0.0-alpha.1",
        "1.0.0-alpha.beta",
        "1.0.0-beta",
        "1.0.0-beta.2",
        "1.0.0-beta.11",
        "1.0.0-rc.1",
        "1.0.0-x-y-z.--",
        "1.0.0",
        "1.0.1-A",
        "1.0.1-Z",
        "1.0.1-a",
        "1.0.1-a-",
        "1.0.1-a-b",
        "1.0.1-a0",
        "1.0.1-aa",
        "1.0.1",
        "1.2.3",
        "1.10.0",
        "2.0.0-123456789012345678901234567890",
        "2.0.0-123456789012345678901234567891",
        "2.0.0",
        "65535.65535.65535",
    ];

    [Fact]
    public void Key_OrdinalOrderMatchesPrecedence()
    {
        var versions = Ascending.Select(SemanticVersion.Parse).ToList();
        foreach (var a in versions)
        {
            foreach (var b in versions)
            {
                var expected = Math.Sign(a.CompareTo(b));
                var actual = Math.Sign(string.CompareOrdinal(NsisVersionOrder.Key(a), NsisVersionOrder.Key(b)));
                Assert.True(expected == actual, $"{a} vs {b}: precedence {expected}, key order {actual}");
            }
        }
    }

    [Fact]
    public void Key_IgnoresBuildMetadata() =>
        Assert.Equal(NsisVersionOrder.Key(SemanticVersion.Parse("1.2.3+a")), NsisVersionOrder.Key(SemanticVersion.Parse("1.2.3+b")));

    [Theory]
    [InlineData("1.2.3", "0000100002000032")]
    [InlineData("1.0.0-alpha.1", "0000100000000001" + "2alpha!" + "10011" + "0")]
    public void Key_Encoding(string version, string expected) =>
        Assert.Equal(expected, NsisVersionOrder.Key(SemanticVersion.Parse(version)));

    [Theory]
    [InlineData("65536.0.0")]
    [InlineData("1.65536.0")]
    [InlineData("1.0.65536")]
    public void Key_CoreAbove65535_Throws(string version) =>
        Assert.Throws<ArgumentException>(() => NsisVersionOrder.Key(SemanticVersion.Parse(version)));

    [Fact]
    public void Key_HugePrerelease_Throws() =>
        Assert.Throws<ArgumentException>(() => NsisVersionOrder.Key(SemanticVersion.Parse("1.0.0-" + new string('a', 600))));
}
