using Zexoro.Platforms;

namespace Zexoro.Core.Tests;

public class TargetPlatformTests
{
    public static TheoryData<string> AllRids()
    {
        var data = new TheoryData<string>();
        foreach (var os in OperatingSystemFamily.All)
        {
            foreach (var architecture in Architecture.All)
            {
                data.Add($"{os.RidName}-{architecture.Name}");
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllRids))]
    public void Parse_RoundTripsThroughToString(string rid)
    {
        var platform = TargetPlatform.Parse(rid);

        Assert.Equal(rid, platform.ToString());
        Assert.Equal(rid, platform.Rid);
        Assert.Equal(platform, TargetPlatform.Parse(platform.ToString()));
    }

    [Theory]
    [InlineData("win-x64", "windows", "x64")]
    [InlineData("osx-arm64", "macos", "arm64")]
    [InlineData("linux-x64", "linux", "x64")]
    [InlineData("android-arm64", "android", "arm64")]
    [InlineData("ios-arm64", "ios", "arm64")]
    [InlineData("WIN-X86", "windows", "x86")]
    [InlineData("windows-arm64", "windows", "arm64")]
    [InlineData("macos-x64", "macos", "x64")]
    public void Parse_KnownRid_ReturnsOperatingSystemAndArchitecture(string text, string os, string architecture)
    {
        var platform = TargetPlatform.Parse(text);

        Assert.Equal(os, platform.OperatingSystem.Name);
        Assert.Equal(architecture, platform.Architecture.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("win")]
    [InlineData("-x64")]
    [InlineData("win-")]
    [InlineData("win-x65")]
    [InlineData("beos-x64")]
    [InlineData("win_x64")]
    [InlineData("win-x64-extra")]
    public void TryParse_InvalidText_ReturnsFalse(string text)
    {
        Assert.False(TargetPlatform.TryParse(text, out _));
    }

    [Fact]
    public void TryParse_Null_ReturnsFalse()
    {
        Assert.False(TargetPlatform.TryParse(null, out _));
    }

    [Fact]
    public void Parse_InvalidText_ThrowsFormatExceptionNamingTheText()
    {
        var exception = Assert.Throws<FormatException>(() => TargetPlatform.Parse("beos-x64"));

        Assert.Contains("'beos-x64'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Equality_ComparesOperatingSystemAndArchitecture()
    {
        var a = new TargetPlatform(OperatingSystemFamily.Windows, Architecture.X64);
        var b = TargetPlatform.Parse("windows-x64");
        var c = new TargetPlatform(OperatingSystemFamily.Windows, Architecture.Arm64);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.True(a != c);
    }

    [Fact]
    public void Current_MatchesHostRuntime()
    {
        var current = TargetPlatform.Current;

        Assert.Equal(OperatingSystem.IsWindows(), current.OperatingSystem == OperatingSystemFamily.Windows);
        Assert.Equal(OperatingSystem.IsMacOS(), current.OperatingSystem == OperatingSystemFamily.MacOS);
        Assert.Equal(OperatingSystem.IsLinux(), current.OperatingSystem == OperatingSystemFamily.Linux);
        Assert.StartsWith(
            current.Rid,
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Default_HasEmptyNames()
    {
        Assert.Equal(string.Empty, default(OperatingSystemFamily).ToString());
        Assert.Equal(string.Empty, default(Architecture).ToString());
        Assert.NotEqual(default, OperatingSystemFamily.Windows);
    }
}

public class OperatingSystemFamilyTests
{
    [Theory]
    [InlineData("windows")]
    [InlineData("win")]
    [InlineData("Windows")]
    public void Parse_NameOrRidName_ReturnsWindows(string text)
    {
        Assert.Equal(OperatingSystemFamily.Windows, OperatingSystemFamily.Parse(text));
    }

    [Fact]
    public void All_IncludesMobileFamilies()
    {
        Assert.Contains(OperatingSystemFamily.Android, OperatingSystemFamily.All);
        Assert.Contains(OperatingSystemFamily.IOS, OperatingSystemFamily.All);
    }

    [Fact]
    public void Parse_Unknown_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => OperatingSystemFamily.Parse("plan9"));
    }
}

public class ArchitectureTests
{
    [Theory]
    [InlineData("x64")]
    [InlineData("x86")]
    [InlineData("arm64")]
    [InlineData("arm")]
    public void Parse_RoundTripsThroughToString(string name)
    {
        Assert.Equal(name, Architecture.Parse(name).ToString());
    }

    [Fact]
    public void Parse_Unknown_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => Architecture.Parse("sparc"));
    }
}
