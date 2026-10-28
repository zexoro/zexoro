using System.Buffers.Binary;

namespace Zexoro.Formats.MacOS;

/// <summary>
/// Writes universal ("fat") Mach-O files from thin 64-bit slices, as <c>lipo -create</c> does
/// (docs/design/0006-desktop-formats.md section 2): the big-endian header <c>0xCAFEBABE</c> with the
/// slice count, one <c>fat_arch</c> per slice (CPU type and subtype copied from the slice's own
/// header, offset, size, alignment as a power of two), then the slices, each at an offset aligned
/// to its alignment: 2^14 (16 KiB pages) for arm64, 2^12 for x86_64. Slices are ordered by CPU type,
/// so x86_64 comes before arm64, as lipo writes them.
/// </summary>
public static class FatMachO
{
    internal const uint FatMagic = 0xCAFEBABE;
    internal const uint MachOMagic64 = 0xFEEDFACF;
    internal const uint CpuTypeX64 = 0x01000007;
    internal const uint CpuTypeArm64 = 0x0100000C;

    /// <summary>Returns the universal file holding <paramref name="slices"/>.</summary>
    /// <exception cref="ArgumentException">A slice is not a thin little-endian 64-bit Mach-O file, or two slices have the same CPU type.</exception>
    public static byte[] Create(IReadOnlyList<byte[]> slices)
    {
        ArgumentNullException.ThrowIfNull(slices);
        if (slices.Count == 0)
        {
            throw new ArgumentException("A universal file needs at least one slice.", nameof(slices));
        }

        var parsed = new List<(uint CpuType, uint CpuSubtype, int Align, byte[] Bytes)>();
        foreach (var slice in slices)
        {
            if (slice.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(slice) != MachOMagic64)
            {
                throw new ArgumentException("A slice is not a thin 64-bit little-endian Mach-O file.", nameof(slices));
            }

            var cpuType = BinaryPrimitives.ReadUInt32LittleEndian(slice.AsSpan(4));
            var cpuSubtype = BinaryPrimitives.ReadUInt32LittleEndian(slice.AsSpan(8));
            if (parsed.Any(p => p.CpuType == cpuType))
            {
                throw new ArgumentException($"Two slices have CPU type 0x{cpuType:X8}.", nameof(slices));
            }

            parsed.Add((cpuType, cpuSubtype, cpuType == CpuTypeArm64 ? 14 : 12, slice));
        }

        parsed.Sort((a, b) => a.CpuType.CompareTo(b.CpuType));
        var offsets = new long[parsed.Count];
        long position = 8 + (20 * parsed.Count);
        for (var i = 0; i < parsed.Count; i++)
        {
            var alignment = 1L << parsed[i].Align;
            position = (position + alignment - 1) / alignment * alignment;
            offsets[i] = position;
            position += parsed[i].Bytes.Length;
        }

        if (position > uint.MaxValue)
        {
            throw new ArgumentException("The slices are too large for a 32-bit universal file.", nameof(slices));
        }

        var result = new byte[position];
        BinaryPrimitives.WriteUInt32BigEndian(result, FatMagic);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), (uint)parsed.Count);
        for (var i = 0; i < parsed.Count; i++)
        {
            var entry = result.AsSpan(8 + (20 * i));
            BinaryPrimitives.WriteUInt32BigEndian(entry, parsed[i].CpuType);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], parsed[i].CpuSubtype);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(entry[12..], (uint)parsed[i].Bytes.Length);
            BinaryPrimitives.WriteUInt32BigEndian(entry[16..], (uint)parsed[i].Align);
            parsed[i].Bytes.CopyTo(result, offsets[i]);
        }

        return result;
    }

    /// <summary>Whether the first bytes are a Mach-O header, thin (32 or 64 bit, either byte order) or universal.</summary>
    public static bool IsMachO(ReadOnlySpan<byte> start)
    {
        if (start.Length < 8)
        {
            return false;
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(start);
        if (magic is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE)
        {
            return true;
        }

        // 0xCAFEBABE is also a Java class file, whose next word (the class file version) is above 44;
        // a universal file has a few slices. 0xCAFEBABF is the 64-bit universal header.
        return magic is FatMagic or 0xCAFEBABF && BinaryPrimitives.ReadUInt32BigEndian(start[4..]) is > 0 and < 32;
    }

    /// <summary>Whether the file at <paramref name="path"/> starts with a Mach-O header.</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static bool IsMachOFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> start = stackalloc byte[8];
        var length = stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
        return IsMachO(start[..length]);
    }
}
