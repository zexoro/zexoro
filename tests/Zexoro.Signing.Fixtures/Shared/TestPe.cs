using System.Buffers.Binary;

namespace Zexoro.Signing.Fixtures;

/// <summary>Builds small, well-formed PE images for tests: headers, a code section, a data section and an optional overlay.</summary>
public static class TestPe
{
    public const int PeHeaderOffset = 0x80;
    public const int SizeOfHeaders = 0x200;
    public const int SectionRawSize = 0x200;

    /// <summary>Offset of the optional header.</summary>
    public const int OptionalHeaderOffset = PeHeaderOffset + 4 + 20;

    /// <summary>Offset of the CheckSum field (same for PE32 and PE32+).</summary>
    public const int CheckSumOffset = OptionalHeaderOffset + 64;

    public static int CertificateEntryOffset(bool pe32Plus) => OptionalHeaderOffset + (pe32Plus ? 112 : 96) + (4 * 8);

    /// <summary>Builds an image.</summary>
    /// <param name="pe32Plus">PE32+ (x64) when true, PE32 (x86) otherwise.</param>
    /// <param name="overlayLength">Bytes of extra data after the last section (an installer payload, for example).</param>
    /// <param name="seed">Varies the section contents.</param>
    public static byte[] Build(bool pe32Plus, int overlayLength = 0, uint seed = 1)
    {
        const int sectionCount = 2;
        var optionalHeaderSize = pe32Plus ? 240 : 224;
        var image = new byte[SizeOfHeaders + (sectionCount * SectionRawSize) + overlayLength];
        var span = image.AsSpan();

        // DOS header and stub.
        span[0] = (byte)'M';
        span[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(span[0x3C..], PeHeaderOffset);

        // PE signature and COFF header.
        "PE\0\0"u8.CopyTo(span[PeHeaderOffset..]);
        var coff = span[(PeHeaderOffset + 4)..];
        BinaryPrimitives.WriteUInt16LittleEndian(coff, pe32Plus ? (ushort)0x8664 : (ushort)0x014C);
        BinaryPrimitives.WriteUInt16LittleEndian(coff[2..], sectionCount);
        BinaryPrimitives.WriteUInt32LittleEndian(coff[4..], 0x6500_0000);
        BinaryPrimitives.WriteUInt16LittleEndian(coff[16..], (ushort)optionalHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(coff[18..], pe32Plus ? (ushort)0x0022 : (ushort)0x0102);

        // Optional header.
        var opt = span[OptionalHeaderOffset..];
        BinaryPrimitives.WriteUInt16LittleEndian(opt, pe32Plus ? (ushort)0x20B : (ushort)0x10B);
        opt[2] = 14;
        BinaryPrimitives.WriteUInt32LittleEndian(opt[4..], SectionRawSize); // SizeOfCode
        BinaryPrimitives.WriteUInt32LittleEndian(opt[8..], SectionRawSize); // SizeOfInitializedData
        BinaryPrimitives.WriteUInt32LittleEndian(opt[16..], 0x1000); // AddressOfEntryPoint
        BinaryPrimitives.WriteUInt32LittleEndian(opt[20..], 0x1000); // BaseOfCode
        if (pe32Plus)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(opt[24..], 0x1_4000_0000);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(opt[24..], 0x2000); // BaseOfData
            BinaryPrimitives.WriteUInt32LittleEndian(opt[28..], 0x40_0000);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(opt[32..], 0x1000); // SectionAlignment
        BinaryPrimitives.WriteUInt32LittleEndian(opt[36..], 0x200); // FileAlignment
        BinaryPrimitives.WriteUInt16LittleEndian(opt[40..], 6); // MajorOperatingSystemVersion
        BinaryPrimitives.WriteUInt16LittleEndian(opt[48..], 6); // MajorSubsystemVersion
        BinaryPrimitives.WriteUInt32LittleEndian(opt[56..], 0x3000); // SizeOfImage
        BinaryPrimitives.WriteUInt32LittleEndian(opt[60..], SizeOfHeaders);
        BinaryPrimitives.WriteUInt16LittleEndian(opt[68..], 3); // Subsystem: console
        BinaryPrimitives.WriteUInt16LittleEndian(opt[70..], 0x8100); // NX compatible, terminal server aware
        if (pe32Plus)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(opt[72..], 0x10_0000);
            BinaryPrimitives.WriteUInt64LittleEndian(opt[80..], 0x1000);
            BinaryPrimitives.WriteUInt64LittleEndian(opt[88..], 0x10_0000);
            BinaryPrimitives.WriteUInt64LittleEndian(opt[96..], 0x1000);
            BinaryPrimitives.WriteUInt32LittleEndian(opt[108..], 16);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(opt[72..], 0x10_0000);
            BinaryPrimitives.WriteUInt32LittleEndian(opt[76..], 0x1000);
            BinaryPrimitives.WriteUInt32LittleEndian(opt[80..], 0x10_0000);
            BinaryPrimitives.WriteUInt32LittleEndian(opt[84..], 0x1000);
            BinaryPrimitives.WriteUInt32LittleEndian(opt[92..], 16);
        }

        // Section table.
        var sections = span[(OptionalHeaderOffset + optionalHeaderSize)..];
        WriteSection(sections, ".text", virtualAddress: 0x1000, rawOffset: SizeOfHeaders, characteristics: 0x6000_0020);
        WriteSection(sections[40..], ".rdata", virtualAddress: 0x2000, rawOffset: SizeOfHeaders + SectionRawSize, characteristics: 0x4000_0040);

        // .text: xor eax, eax; ret — valid for x86 and x64.
        span[SizeOfHeaders] = 0x31;
        span[SizeOfHeaders + 1] = 0xC0;
        span[SizeOfHeaders + 2] = 0xC3;

        // .rdata and the overlay: deterministic filler.
        var state = seed;
        foreach (ref var b in span[(SizeOfHeaders + SectionRawSize)..])
        {
            state = unchecked((state * 1_664_525) + 1_013_904_223);
            b = (byte)(state >> 24);
        }

        return image;
    }

    private static void WriteSection(Span<byte> header, string name, uint virtualAddress, uint rawOffset, uint characteristics)
    {
        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], SectionRawSize); // VirtualSize
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], virtualAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], SectionRawSize); // SizeOfRawData
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], rawOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header[36..], characteristics);
    }
}
