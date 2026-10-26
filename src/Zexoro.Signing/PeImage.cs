using System.Buffers.Binary;

namespace Zexoro.Signing;

/// <summary>
/// The parts of a PE (Portable Executable) file that Authenticode needs: where the checksum
/// and the certificate table data directory entry live, and where the certificate table is.
/// </summary>
/// <remarks>
/// Layout references: Microsoft "PE Format" and "Windows Authenticode Portable Executable
/// Signature Format". All offsets are file offsets.
/// </remarks>
internal sealed class PeImage
{
    internal const ushort Pe32Magic = 0x10B;
    internal const ushort Pe32PlusMagic = 0x20B;

    private const int CertificateTableIndex = 4;
    private const int DataDirectoryEntrySize = 8;
    private const int CoffHeaderSize = 20;
    private const int SectionHeaderSize = 40;

    private PeImage()
    {
    }

    /// <summary>Total length of the file in bytes.</summary>
    public long FileLength { get; private init; }

    /// <summary>True for PE32+ (64-bit) images, false for PE32.</summary>
    public bool IsPe32Plus { get; private init; }

    /// <summary>Offset of the 4-byte <c>CheckSum</c> field of the optional header.</summary>
    public long CheckSumOffset { get; private init; }

    /// <summary>Offset of the 8-byte certificate table entry in the data directories.</summary>
    public long CertificateTableEntryOffset { get; private init; }

    /// <summary>Value of <c>SizeOfHeaders</c>.</summary>
    public long SizeOfHeaders { get; private init; }

    /// <summary>File offset of the attribute certificate table, or 0 when the file has none.</summary>
    public long CertificateTableOffset { get; private init; }

    /// <summary>Size of the attribute certificate table, or 0 when the file has none.</summary>
    public long CertificateTableSize { get; private init; }

    /// <summary>The section headers in file order of their raw data.</summary>
    public IReadOnlyList<PeSection> Sections { get; private init; } = [];

    public bool HasCertificateTable => CertificateTableSize != 0;

    /// <summary>
    /// End of the data covered by the Authenticode digest: the start of the certificate table,
    /// or the end of the file when there is none.
    /// </summary>
    public long SignedDataEnd => HasCertificateTable ? CertificateTableOffset : FileLength;

    /// <summary>Reads and validates the headers of the PE file in <paramref name="stream"/>.</summary>
    /// <exception cref="SigningException">The stream does not hold a PE file this signer supports.</exception>
    public static PeImage Read(Stream stream)
    {
        var fileLength = stream.Length;

        Span<byte> dos = stackalloc byte[64];
        ReadAt(stream, 0, dos, "the DOS header");
        if (dos[0] != (byte)'M' || dos[1] != (byte)'Z')
        {
            throw Invalid("it does not start with the 'MZ' signature");
        }

        var peOffset = BinaryPrimitives.ReadUInt32LittleEndian(dos[0x3C..]);
        if (peOffset < 0x40 || peOffset > fileLength - 4 - CoffHeaderSize)
        {
            throw Invalid("the PE header offset points outside the file");
        }

        Span<byte> coff = stackalloc byte[4 + CoffHeaderSize];
        ReadAt(stream, peOffset, coff, "the COFF header");
        if (!coff[..4].SequenceEqual("PE\0\0"u8))
        {
            throw Invalid("the 'PE' signature is missing");
        }

        var numberOfSections = BinaryPrimitives.ReadUInt16LittleEndian(coff[(4 + 2)..]);
        var sizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(coff[(4 + 16)..]);
        var optionalHeaderOffset = peOffset + 4 + CoffHeaderSize;
        if (optionalHeaderOffset + sizeOfOptionalHeader > fileLength)
        {
            throw Invalid("the optional header extends past the end of the file");
        }

        var optional = new byte[sizeOfOptionalHeader];
        ReadAt(stream, optionalHeaderOffset, optional, "the optional header");
        if (sizeOfOptionalHeader < 2)
        {
            throw Invalid("the optional header is missing");
        }

        var magic = BinaryPrimitives.ReadUInt16LittleEndian(optional);
        bool isPe32Plus;
        int numberOfRvaAndSizesOffset;
        if (magic == Pe32Magic)
        {
            isPe32Plus = false;
            numberOfRvaAndSizesOffset = 92;
        }
        else if (magic == Pe32PlusMagic)
        {
            isPe32Plus = true;
            numberOfRvaAndSizesOffset = 108;
        }
        else
        {
            throw Invalid($"the optional header magic 0x{magic:X4} is neither PE32 nor PE32+");
        }

        var dataDirectoriesOffset = numberOfRvaAndSizesOffset + 4;
        var certificateEntryOffset = dataDirectoriesOffset + (CertificateTableIndex * DataDirectoryEntrySize);
        if (sizeOfOptionalHeader < certificateEntryOffset + DataDirectoryEntrySize)
        {
            throw Invalid("the optional header has no certificate table entry");
        }

        var numberOfRvaAndSizes = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(numberOfRvaAndSizesOffset));
        if (numberOfRvaAndSizes <= CertificateTableIndex)
        {
            throw Invalid("the optional header has no certificate table entry");
        }

        var sizeOfHeaders = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(60));
        var certificateEntryFileOffset = optionalHeaderOffset + certificateEntryOffset;
        if (sizeOfHeaders < certificateEntryFileOffset + DataDirectoryEntrySize || sizeOfHeaders > fileLength)
        {
            throw Invalid("SizeOfHeaders is inconsistent with the headers");
        }

        long certificateTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(certificateEntryOffset));
        long certificateTableSize = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(certificateEntryOffset + 4));
        if (certificateTableSize == 0)
        {
            certificateTableOffset = 0;
        }
        else
        {
            if (certificateTableOffset < sizeOfHeaders || certificateTableOffset + certificateTableSize > fileLength)
            {
                throw Invalid("the certificate table points outside the file");
            }

            if (certificateTableOffset + certificateTableSize != fileLength)
            {
                throw Invalid("there is data after the certificate table");
            }
        }

        var sectionTableOffset = optionalHeaderOffset + sizeOfOptionalHeader;
        var sectionTableSize = numberOfSections * SectionHeaderSize;
        if (sectionTableOffset + sectionTableSize > fileLength)
        {
            throw Invalid("the section table extends past the end of the file");
        }

        var sectionTable = new byte[sectionTableSize];
        ReadAt(stream, sectionTableOffset, sectionTable, "the section table");
        var sections = new List<PeSection>(numberOfSections);
        for (var i = 0; i < numberOfSections; i++)
        {
            var header = sectionTable.AsSpan(i * SectionHeaderSize, SectionHeaderSize);
            var name = System.Text.Encoding.ASCII.GetString(header[..8]).TrimEnd('\0');
            var sizeOfRawData = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
            var pointerToRawData = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            sections.Add(new PeSection(name, pointerToRawData, sizeOfRawData));
        }

        sections.Sort((a, b) => a.PointerToRawData.CompareTo(b.PointerToRawData));

        return new PeImage
        {
            FileLength = fileLength,
            IsPe32Plus = isPe32Plus,
            CheckSumOffset = optionalHeaderOffset + 64,
            CertificateTableEntryOffset = certificateEntryFileOffset,
            SizeOfHeaders = sizeOfHeaders,
            CertificateTableOffset = certificateTableOffset,
            CertificateTableSize = certificateTableSize,
            Sections = sections,
        };
    }

    internal static void ReadAt(Stream stream, long offset, Span<byte> buffer, string what)
    {
        stream.Position = offset;
        try
        {
            stream.ReadExactly(buffer);
        }
        catch (EndOfStreamException ex)
        {
            throw new SigningException($"Not a valid PE file: the file ends inside {what}.", ex);
        }
    }

    private static SigningException Invalid(string reason) => new($"Not a valid PE file: {reason}.");
}

/// <summary>A section header: where the section's raw data lives in the file.</summary>
internal readonly record struct PeSection(string Name, long PointerToRawData, long SizeOfRawData);
