using System.Buffers.Binary;
using System.Diagnostics;
using Zexoro.Formats.Linux;

namespace Zexoro.Formats.MacOS.Tests;

public sealed class IcnsTests
{
    [Fact]
    public void Create_WritesPngElementsByAscendingSize()
    {
        var small = TestImages.Png(32);
        var large = TestImages.Png(256);

        var icns = Icns.Create([PngIcon.FromBytes(large), PngIcon.FromBytes(small)]);

        Assert.True(Icns.IsIcns(icns));
        Assert.Equal("icns"u8.ToArray(), icns[..4]);
        Assert.Equal((uint)icns.Length, BinaryPrimitives.ReadUInt32BigEndian(icns.AsSpan(4)));
        Assert.Equal("icp5"u8.ToArray(), icns[8..12]);
        Assert.Equal((uint)(8 + small.Length), BinaryPrimitives.ReadUInt32BigEndian(icns.AsSpan(12)));
        Assert.Equal(small, icns.AsSpan(16, small.Length).ToArray());
        var second = 16 + small.Length;
        Assert.Equal("ic08"u8.ToArray(), icns[second..(second + 4)]);
        Assert.Equal(large, icns.AsSpan(second + 8).ToArray());
    }

    [Theory]
    [InlineData(48, 48)]
    [InlineData(64, 32)]
    public void Create_RejectsSizesWithoutAnIcnsType(int width, int height)
    {
        var error = Assert.Throws<IconException>(() => Icns.Create([new PngImage(TestImages.Png(16), width, height)]));

        Assert.Contains($"a {width}x{height} image", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsIcns_ChecksMagicAndLength()
    {
        var icns = Icns.Create([PngIcon.FromBytes(TestImages.Png(16))]);

        Assert.True(Icns.IsIcns(icns));
        Assert.False(Icns.IsIcns(icns.AsSpan(0, icns.Length - 1)));
        Assert.False(Icns.IsIcns("icnx\0\0\0\b"u8));
    }

    /// <summary>iconutil, the system tool, unpacks the icns into an iconset with the right image.</summary>
    [Fact]
    public void Create_IsReadByIconutil()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "iconutil exists on macOS only.");
        var directory = Directory.CreateTempSubdirectory("zexoro-icns-");
        try
        {
            var path = Path.Combine(directory.FullName, "app.icns");
            File.WriteAllBytes(path, Icns.Create([PngIcon.FromBytes(TestImages.Png(128))]));
            var iconset = Path.Combine(directory.FullName, "app.iconset");
            using var iconutil = Process.Start(new ProcessStartInfo("iconutil", ["-c", "iconset", path, "-o", iconset]) { RedirectStandardError = true })!;
            var error = iconutil.StandardError.ReadToEnd();
            iconutil.WaitForExit();
            Assert.True(iconutil.ExitCode == 0, error);
            Assert.True(File.Exists(Path.Combine(iconset, "icon_128x128.png")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
