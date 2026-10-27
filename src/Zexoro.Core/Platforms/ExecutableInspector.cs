using System.Buffers.Binary;

namespace Zexoro.Platforms;

/// <summary>The binary format of an executable.</summary>
public enum ExecutableFormat
{
    /// <summary>Not a recognized executable (a script, data, or a truncated file).</summary>
    Unknown,

    /// <summary>Windows Portable Executable.</summary>
    Pe,

    /// <summary>Executable and Linkable Format, read as Linux.</summary>
    Elf,

    /// <summary>Mach-O, thin or fat (universal), read as macOS.</summary>
    MachO,
}

/// <summary>What <see cref="ExecutableInspector.Inspect(string)"/> found.</summary>
/// <param name="Format">The binary format.</param>
/// <param name="Target">The target platform, or null when the format or its architecture is not supported.</param>
/// <param name="Description">A phrase for messages, e.g. <c>an ELF executable for machine 0x28</c>.</param>
public sealed record ExecutableInfo(ExecutableFormat Format, TargetPlatform? Target, string Description);

/// <summary>
/// Reads the target platform of an executable from its header: PE (COFF machine), ELF
/// (<c>e_machine</c>, with class and byte order checked) or Mach-O (<c>cputype</c>; a fat binary
/// with x64 and arm64 slices is <c>osx-universal</c>).
/// </summary>
/// <remarks>
/// ELF files are taken to be Linux and Mach-O files macOS: the headers do not say reliably which
/// operating system they are for, and those are the only ELF and Mach-O targets zexoro packs.
/// </remarks>
public static class ExecutableInspector
{
    private const ushort ElfTypeExecutable = 2;
    private const ushort ElfTypeShared = 3;
    private const ushort ElfMachineX64 = 62;
    private const ushort ElfMachineArm64 = 183;

    private const uint MachOMagic32 = 0xFEEDFACE;
    private const uint MachOMagic64 = 0xFEEDFACF;
    private const uint FatMagic = 0xCAFEBABE;
    private const uint FatMagic64 = 0xCAFEBABF;
    private const uint CpuTypeX64 = 0x01000007;
    private const uint CpuTypeArm64 = 0x0100000C;

    /// <summary>
    /// The most slices a fat header may list. Java class files share the magic 0xCAFEBABE and have
    /// their version (45 or more) where a fat header has the slice count.
    /// </summary>
    private const uint MaxFatSlices = 20;

    /// <summary>Inspects the file at <paramref name="path"/>.</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be opened.</exception>
    public static ExecutableInfo Inspect(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var stream = File.OpenRead(path);
        return Inspect(stream);
    }

    /// <summary>Inspects a seekable stream from its start.</summary>
    /// <exception cref="IOException">The stream cannot be read.</exception>
    public static ExecutableInfo Inspect(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (PeMachine.TryReadMachine(stream, out var machine))
        {
            return PeMachine.ToArchitecture(machine) is { } architecture
                ? Found(ExecutableFormat.Pe, OperatingSystemFamily.Windows, architecture, $"a Windows {architecture.Name} executable (PE)")
                : Unsupported(ExecutableFormat.Pe, $"a Windows executable (PE) for machine 0x{machine:X4}");
        }

        stream.Position = 0;
        Span<byte> header = stackalloc byte[64];
        var length = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        header = header[..length];
        if (length >= 20 && header[..4].SequenceEqual("\u007FELF"u8))
        {
            return InspectElf(header);
        }

        if (length >= 8)
        {
            var magic = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (magic is FatMagic or FatMagic64)
            {
                var count = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
                if (count is > 0 and <= MaxFatSlices)
                {
                    return InspectFat(stream, count, magic == FatMagic64);
                }
            }

            var little = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (little is MachOMagic32 or MachOMagic64)
            {
                return InspectThin(BinaryPrimitives.ReadUInt32LittleEndian(header[4..]));
            }

            if (magic is MachOMagic32 or MachOMagic64)
            {
                return InspectThin(BinaryPrimitives.ReadUInt32BigEndian(header[4..]));
            }
        }

        return Unsupported(ExecutableFormat.Unknown, "not a PE, ELF or Mach-O executable");
    }

    private static ExecutableInfo InspectElf(ReadOnlySpan<byte> header)
    {
        var is64 = header[4] == 2;
        var littleEndian = header[5] == 1;
        if (header[4] is not (1 or 2) || header[5] is not (1 or 2) || header[6] != 1)
        {
            return Unsupported(ExecutableFormat.Elf, "an ELF file with an invalid header");
        }

        var type = littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(header[16..]) : BinaryPrimitives.ReadUInt16BigEndian(header[16..]);
        var machine = littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(header[18..]) : BinaryPrimitives.ReadUInt16BigEndian(header[18..]);
        if (type is not (ElfTypeExecutable or ElfTypeShared))
        {
            return Unsupported(ExecutableFormat.Elf, $"an ELF file that is not an executable (type {type})");
        }

        Architecture? architecture = machine switch
        {
            ElfMachineX64 when is64 && littleEndian => Architecture.X64,
            ElfMachineArm64 when is64 && littleEndian => Architecture.Arm64,
            _ => null,
        };
        var layout = $"{(is64 ? "64" : "32")}-bit {(littleEndian ? "little" : "big")}-endian";
        return architecture is { } found
            ? Found(ExecutableFormat.Elf, OperatingSystemFamily.Linux, found, $"a Linux {found.Name} executable (ELF)")
            : Unsupported(ExecutableFormat.Elf, $"an ELF executable for machine {machine} ({layout})");
    }

    private static ExecutableInfo InspectThin(uint cpuType) => ToArchitecture(cpuType) is { } architecture
        ? Found(ExecutableFormat.MachO, OperatingSystemFamily.MacOS, architecture, $"a macOS {architecture.Name} executable (Mach-O)")
        : Unsupported(ExecutableFormat.MachO, $"a Mach-O executable for CPU type 0x{cpuType:X8}");

    private static ExecutableInfo InspectFat(Stream stream, uint count, bool fat64)
    {
        var entrySize = fat64 ? 32 : 20;
        var entries = new byte[count * entrySize];
        stream.Position = 8;
        if (stream.ReadAtLeast(entries, entries.Length, throwOnEndOfStream: false) < entries.Length)
        {
            return Unsupported(ExecutableFormat.MachO, "a truncated universal Mach-O file");
        }

        var x64 = false;
        var arm64 = false;
        for (var i = 0; i < count; i++)
        {
            var cpuType = BinaryPrimitives.ReadUInt32BigEndian(entries.AsSpan(i * entrySize));
            x64 |= cpuType == CpuTypeX64;
            arm64 |= cpuType == CpuTypeArm64;
        }

        return (x64, arm64) switch
        {
            (true, true) => Found(ExecutableFormat.MachO, OperatingSystemFamily.MacOS, Architecture.Universal, "a macOS universal executable (Mach-O, x64 and arm64)"),
            (true, false) => Found(ExecutableFormat.MachO, OperatingSystemFamily.MacOS, Architecture.X64, "a macOS x64 executable (universal Mach-O with an x64 slice only)"),
            (false, true) => Found(ExecutableFormat.MachO, OperatingSystemFamily.MacOS, Architecture.Arm64, "a macOS arm64 executable (universal Mach-O with an arm64 slice only)"),
            _ => Unsupported(ExecutableFormat.MachO, "a universal Mach-O file without an x64 or arm64 slice"),
        };
    }

    private static Architecture? ToArchitecture(uint cpuType) => cpuType switch
    {
        CpuTypeX64 => Architecture.X64,
        CpuTypeArm64 => Architecture.Arm64,
        _ => null,
    };

    private static ExecutableInfo Found(ExecutableFormat format, OperatingSystemFamily os, Architecture architecture, string description) =>
        new(format, new TargetPlatform(os, architecture), description);

    private static ExecutableInfo Unsupported(ExecutableFormat format, string description) => new(format, null, description);
}
