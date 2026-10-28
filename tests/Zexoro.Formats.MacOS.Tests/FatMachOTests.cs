using System.Buffers.Binary;
using System.Diagnostics;

namespace Zexoro.Formats.MacOS.Tests;

public sealed class FatMachOTests
{
    /// <summary>A thin 64-bit Mach-O header (no load commands) followed by filler bytes.</summary>
    internal static byte[] Thin(uint cpuType, uint cpuSubtype, int length = 4096)
    {
        var bytes = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), cpuType);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), cpuSubtype);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 2); // MH_EXECUTE
        bytes[^1] = (byte)cpuType;
        return bytes;
    }

    [Fact]
    public void Create_WritesHeaderAndAlignedSlicesInCpuTypeOrder()
    {
        var arm64 = Thin(FatMachO.CpuTypeArm64, 0, 5000);
        var x64 = Thin(FatMachO.CpuTypeX64, 3, 3000);

        var fat = FatMachO.Create([arm64, x64]);

        Assert.Equal(0xCAFEBABEu, BinaryPrimitives.ReadUInt32BigEndian(fat));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(fat.AsSpan(4)));
        (uint Cpu, uint Sub, uint Offset, uint Size, uint Align) Entry(int i)
        {
            var e = fat.AsSpan(8 + (20 * i));
            return (BinaryPrimitives.ReadUInt32BigEndian(e), BinaryPrimitives.ReadUInt32BigEndian(e[4..]), BinaryPrimitives.ReadUInt32BigEndian(e[8..]),
                BinaryPrimitives.ReadUInt32BigEndian(e[12..]), BinaryPrimitives.ReadUInt32BigEndian(e[16..]));
        }

        Assert.Equal((FatMachO.CpuTypeX64, 3u, 4096u, 3000u, 12u), Entry(0));
        Assert.Equal((FatMachO.CpuTypeArm64, 0u, 16384u, 5000u, 14u), Entry(1));
        Assert.Equal(x64, fat.AsSpan(4096, 3000).ToArray());
        Assert.Equal(arm64, fat.AsSpan(16384, 5000).ToArray());
        Assert.Equal(16384 + 5000, fat.Length);
        Assert.True(FatMachO.IsMachO(fat));
    }

    [Fact]
    public void Create_IsReadAsUniversalByTheInspector()
    {
        var fat = FatMachO.Create([Thin(FatMachO.CpuTypeX64, 3), Thin(FatMachO.CpuTypeArm64, 0)]);

        Assert.Equal("osx-universal", Platforms.ExecutableInspector.Inspect(new MemoryStream(fat)).Target?.Rid);
    }

    [Fact]
    public void Create_RejectsDuplicatesAndNonMachO()
    {
        Assert.Throws<ArgumentException>(() => FatMachO.Create([Thin(FatMachO.CpuTypeX64, 3), Thin(FatMachO.CpuTypeX64, 3)]));
        Assert.Throws<ArgumentException>(() => FatMachO.Create([new byte[64]]));
        Assert.Throws<ArgumentException>(() => FatMachO.Create([]));
    }

    [Theory]
    [InlineData(new byte[] { 0xCF, 0xFA, 0xED, 0xFE, 7, 0, 0, 1 }, true)]
    [InlineData(new byte[] { 0xFE, 0xED, 0xFA, 0xCE, 0, 0, 0, 7 }, true)]
    [InlineData(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 2 }, true)]
    [InlineData(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 0x41 }, false)] // a Java class file, version 65
    [InlineData(new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0 }, false)]
    public void IsMachO_RecognizesMachOHeaders(byte[] start, bool expected)
    {
        Assert.Equal(expected, FatMachO.IsMachO(start));
    }

    /// <summary>lipo, the system tool, reads the universal file of two real executables.</summary>
    [Fact]
    public void Create_IsAcceptedByLipo()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "lipo exists on macOS only.");
        var directory = Directory.CreateTempSubdirectory("zexoro-fat-");
        try
        {
            var path = Path.Combine(directory.FullName, "fat");
            File.WriteAllBytes(path, FatMachO.Create([Thin(FatMachO.CpuTypeX64, 3), Thin(FatMachO.CpuTypeArm64, 0)]));
            using var lipo = Process.Start(new ProcessStartInfo("lipo", ["-archs", path]) { RedirectStandardOutput = true })!;
            var output = lipo.StandardOutput.ReadToEnd();
            lipo.WaitForExit();
            Assert.Equal(0, lipo.ExitCode);
            Assert.Equal("x86_64 arm64", output.Trim());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
