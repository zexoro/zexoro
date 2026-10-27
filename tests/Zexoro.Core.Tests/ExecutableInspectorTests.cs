using System.Buffers.Binary;
using Zexoro.Platforms;
using Zexoro.Signing.Fixtures;

namespace Zexoro.Core.Tests;

public class ExecutableInspectorTests
{
    /// <summary>A 64-byte ELF header: class, byte order, type and machine; the rest is zero.</summary>
    internal static byte[] Elf(ushort machine, byte elfClass = 2, byte data = 1, ushort type = 3)
    {
        var header = new byte[64];
        "\u007FELF"u8.CopyTo(header);
        header[4] = elfClass;
        header[5] = data;
        header[6] = 1;
        if (data == 1)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(16), type);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(18), machine);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(16), type);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(18), machine);
        }

        return header;
    }

    /// <summary>A thin 64-bit little-endian Mach-O header (magic, cputype, cpusubtype, filetype MH_EXECUTE).</summary>
    internal static byte[] MachO(uint cpuType)
    {
        var header = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), cpuType);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 2);
        return header;
    }

    /// <summary>A fat header (big-endian) with one 20-byte <c>fat_arch</c> per CPU type, followed by padding.</summary>
    internal static byte[] Fat(params uint[] cpuTypes)
    {
        var header = new byte[8 + (20 * cpuTypes.Length) + 64];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)cpuTypes.Length);
        for (var i = 0; i < cpuTypes.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8 + (20 * i)), cpuTypes[i]);
        }

        return header;
    }

    private static ExecutableInfo Inspect(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return ExecutableInspector.Inspect(stream);
    }

    [Theory]
    [InlineData(true, "win-x64")]
    [InlineData(false, "win-x86")]
    public void Inspect_Pe_ReturnsWindowsTarget(bool pe32Plus, string rid)
    {
        var info = Inspect(TestPe.Build(pe32Plus));

        Assert.Equal(ExecutableFormat.Pe, info.Format);
        Assert.Equal(TargetPlatform.Parse(rid), info.Target);
    }

    [Fact]
    public void Inspect_PeForUnsupportedMachine_HasNoTarget()
    {
        var image = TestPe.Build(pe32Plus: false);
        image[TestPe.PeHeaderOffset + 4] = 0xC4;
        image[TestPe.PeHeaderOffset + 5] = 0x01;

        var info = Inspect(image);

        Assert.Equal(ExecutableFormat.Pe, info.Format);
        Assert.Null(info.Target);
        Assert.Contains("0x01C4", info.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(62, "linux-x64")]
    [InlineData(183, "linux-arm64")]
    public void Inspect_Elf_ReturnsLinuxTarget(int machine, string rid)
    {
        var info = Inspect(Elf((ushort)machine));

        Assert.Equal(ExecutableFormat.Elf, info.Format);
        Assert.Equal(TargetPlatform.Parse(rid), info.Target);
    }

    [Fact]
    public void Inspect_ElfExecutableType_IsAccepted() =>
        Assert.Equal(TargetPlatform.Parse("linux-x64"), Inspect(Elf(62, type: 2)).Target);

    [Theory]
    [InlineData(62, 1, 1, 3)] // x86-64 machine in a 32-bit file
    [InlineData(183, 2, 2, 3)] // big-endian AArch64
    [InlineData(40, 1, 1, 3)] // 32-bit ARM
    [InlineData(62, 2, 1, 1)] // relocatable object, not an executable
    [InlineData(62, 3, 1, 3)] // invalid class
    public void Inspect_UnsupportedElf_HasNoTarget(int machine, int elfClass, int data, int type)
    {
        var info = Inspect(Elf((ushort)machine, (byte)elfClass, (byte)data, (ushort)type));

        Assert.Equal(ExecutableFormat.Elf, info.Format);
        Assert.Null(info.Target);
    }

    [Theory]
    [InlineData(0x01000007u, "osx-x64")]
    [InlineData(0x0100000Cu, "osx-arm64")]
    public void Inspect_ThinMachO_ReturnsMacOSTarget(uint cpuType, string rid)
    {
        var info = Inspect(MachO(cpuType));

        Assert.Equal(ExecutableFormat.MachO, info.Format);
        Assert.Equal(TargetPlatform.Parse(rid), info.Target);
    }

    [Fact]
    public void Inspect_BigEndianMachO_ReadsTheCpuTypeInItsByteOrder()
    {
        var header = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 0x0100000C);

        Assert.Equal(TargetPlatform.Parse("osx-arm64"), Inspect(header).Target);
    }

    [Fact]
    public void Inspect_ThinMachOForUnsupportedCpu_HasNoTarget()
    {
        var info = Inspect(MachO(7)); // i386

        Assert.Equal(ExecutableFormat.MachO, info.Format);
        Assert.Null(info.Target);
    }

    [Fact]
    public void Inspect_FatWithX64AndArm64_IsUniversal()
    {
        var info = Inspect(Fat(0x01000007, 0x0100000C));

        Assert.Equal(new TargetPlatform(OperatingSystemFamily.MacOS, Architecture.Universal), info.Target);
        Assert.Equal("osx-universal", info.Target?.Rid);
    }

    [Theory]
    [InlineData(new uint[] { 0x0100000C }, "osx-arm64")]
    [InlineData(new uint[] { 7, 0x01000007 }, "osx-x64")]
    public void Inspect_FatWithOneSupportedSlice_IsThatArchitecture(uint[] cpuTypes, string rid) =>
        Assert.Equal(TargetPlatform.Parse(rid), Inspect(Fat(cpuTypes)).Target);

    [Fact]
    public void Inspect_FatWithoutSupportedSlices_HasNoTarget()
    {
        var info = Inspect(Fat(7, 18));

        Assert.Equal(ExecutableFormat.MachO, info.Format);
        Assert.Null(info.Target);
    }

    [Fact]
    public void Inspect_FatMagic64_ReadsItsLargerEntries()
    {
        var header = new byte[8 + 64 + 16];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0xCAFEBABF);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), 0x01000007);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8 + 32), 0x0100000C);

        Assert.Equal(Architecture.Universal, Inspect(header).Target?.Architecture);
    }

    [Fact]
    public void Inspect_JavaClassFile_IsNotMachO()
    {
        // Same magic as a fat binary; major version 52 where the slice count would be.
        byte[] header = [0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x34, 0, 0, 0, 0];

        Assert.Equal(ExecutableFormat.Unknown, Inspect(header).Format);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x4D, 0x5A })]
    [InlineData(new byte[] { 0x23, 0x21, 0x2F, 0x62, 0x69, 0x6E, 0x2F, 0x73, 0x68, 0x0A })] // #!/bin/sh
    [InlineData(new byte[] { 0x7F, 0x45, 0x4C, 0x46 })] // truncated ELF
    public void Inspect_NotAnExecutable_IsUnknown(byte[] bytes)
    {
        var info = Inspect(bytes);

        Assert.Equal(ExecutableFormat.Unknown, info.Format);
        Assert.Null(info.Target);
    }

    [Fact]
    public void Inspect_Path_ReadsTheFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Elf(183));
            Assert.Equal(TargetPlatform.Parse("linux-arm64"), ExecutableInspector.Inspect(path).Target);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("/lib64/ld-linux-x86-64.so.2")]
    [InlineData("/lib/ld-musl-aarch64.so.1")]
    public void ReadElfInterpreter_ReturnsThePtInterpPath(string interpreter)
    {
        var bytes = new byte[64 + 56 + interpreter.Length + 1];
        Elf(62).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x20), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x36), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x38), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), 3);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64 + 0x08), 120);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64 + 0x20), (ulong)interpreter.Length + 1);
        System.Text.Encoding.ASCII.GetBytes(interpreter).CopyTo(bytes, 120);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, bytes);
            Assert.Equal(interpreter, ExecutableInspector.ReadElfInterpreter(path));

            File.WriteAllBytes(path, Elf(62));
            Assert.Null(ExecutableInspector.ReadElfInterpreter(path));

            File.WriteAllBytes(path, "#!/bin/sh\n"u8.ToArray());
            Assert.Null(ExecutableInspector.ReadElfInterpreter(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
