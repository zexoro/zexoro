using Zexoro.Versioning;

namespace Zexoro.Formats.Linux.Tests;

public sealed class ApkVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("0.0.1+build.5", "0.0.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0_alpha1")]
    [InlineData("1.0.0-beta.12", "1.0.0_beta12")]
    [InlineData("1.0.0-rc.0", "1.0.0_rc0")]
    [InlineData("10.20.30-rc.4+sha.abc", "10.20.30_rc4")]
    public void From_SupportedVersions_Map(string semver, string expected)
    {
        Assert.Equal(expected, ApkVersion.From(SemanticVersion.Parse(semver)));
    }

    [Theory]
    [InlineData("1.0.0-alpha")]
    [InlineData("1.0.0-preview.1")]
    [InlineData("1.0.0-beta.x")]
    [InlineData("1.0.0-rc.1.2")]
    [InlineData("1.0.0-RC.1")]
    [InlineData("1.0.0-0")]
    public void From_OtherPrereleases_AreRefused(string semver)
    {
        var ex = Assert.Throws<ArgumentException>(() => ApkVersion.From(SemanticVersion.Parse(semver)));

        Assert.Contains("apk packages support the pre-releases alpha.N, beta.N and rc.N", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithRelease_AppendsR0()
    {
        Assert.Equal("1.0.0_beta2-r0", ApkVersion.WithRelease(SemanticVersion.Parse("1.0.0-beta.2")));
    }

    /// <summary>
    /// The mapped versions sort as SemVer sorts them, by apk's rules for these forms: numbers, then
    /// the suffixes _alpha &lt; _beta &lt; _rc &lt; none, each with its number.
    /// </summary>
    [Fact]
    public void From_KeepsTheOrderOfSupportedVersions()
    {
        string[] ordered = ["1.0.0-alpha.1", "1.0.0-alpha.2", "1.0.0-alpha.10", "1.0.0-beta.1", "1.0.0-rc.1", "1.0.0-rc.2", "1.0.0", "1.0.1-alpha.1", "1.0.1", "1.1.0"];
        var mapped = ordered.Select(v => ApkVersion.From(SemanticVersion.Parse(v))).ToList();

        var sorted = mapped.ToList();
        sorted.Sort(Compare);
        Assert.Equal(mapped, sorted);
    }

    private static readonly string[] SuffixOrder = ["alpha", "beta", "rc", string.Empty];

    private static int Compare(string a, string b)
    {
        static (ulong[] Numbers, int Suffix, ulong SuffixNumber) Parse(string v)
        {
            var parts = v.Split('_');
            var numbers = parts[0].Split('.').Select(ulong.Parse).ToArray();
            if (parts.Length == 1)
            {
                return (numbers, 3, 0);
            }

            var name = new string(parts[1].TakeWhile(char.IsAsciiLetter).ToArray());
            return (numbers, Array.IndexOf(SuffixOrder, name), ulong.Parse(parts[1][name.Length..], System.Globalization.CultureInfo.InvariantCulture));
        }

        var (na, sa, ka) = Parse(a);
        var (nb, sb, kb) = Parse(b);
        for (var i = 0; i < 3; i++)
        {
            if (na[i] != nb[i])
            {
                return na[i].CompareTo(nb[i]);
            }
        }

        return sa != sb ? sa.CompareTo(sb) : ka.CompareTo(kb);
    }
}
