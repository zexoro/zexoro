using System.Buffers.Binary;
using System.Text;

namespace Zexoro.Formats.Linux;

/// <summary>Tag numbers of rpm's headers (rpmtag.h), as far as zexoro writes them.</summary>
internal static class RpmTag
{
    // Region tags.
    public const int HeaderSignatures = 62;
    public const int HeaderImmutable = 63;
    public const int HeaderI18nTable = 100;

    // Signature header (RPMSIGTAG_*).
    public const int RsaHeader = 268;
    public const int Sha1Header = 269;
    public const int Sha256Header = 273;
    public const int SignatureSize = 1000;
    public const int SignatureMd5 = 1004;
    public const int SignaturePayloadSize = 1007;

    // Main header (RPMTAG_*).
    public const int Name = 1000;
    public const int Version = 1001;
    public const int Release = 1002;
    public const int Summary = 1004;
    public const int Description = 1005;
    public const int BuildTime = 1006;
    public const int BuildHost = 1007;
    public const int Size = 1009;
    public const int Vendor = 1011;
    public const int License = 1014;
    public const int Packager = 1015;
    public const int Group = 1016;
    public const int Url = 1020;
    public const int Os = 1021;
    public const int Arch = 1022;
    public const int FileSizes = 1028;
    public const int FileModes = 1030;
    public const int FileRdevs = 1033;
    public const int FileMtimes = 1034;
    public const int FileDigests = 1035;
    public const int FileLinkTos = 1036;
    public const int FileFlags = 1037;
    public const int FileUserName = 1039;
    public const int FileGroupName = 1040;
    public const int SourceRpm = 1044;
    public const int FileVerifyFlags = 1045;
    public const int ProvideName = 1047;
    public const int RequireFlags = 1048;
    public const int RequireName = 1049;
    public const int RequireVersion = 1050;
    public const int FileDevices = 1095;
    public const int FileInodes = 1096;
    public const int FileLangs = 1097;
    public const int ProvideFlags = 1112;
    public const int ProvideVersion = 1113;
    public const int DirIndexes = 1116;
    public const int BaseNames = 1117;
    public const int DirNames = 1118;
    public const int PayloadFormat = 1124;
    public const int PayloadCompressor = 1125;
    public const int PayloadFlags = 1126;
    public const int FileDigestAlgo = 5011;
    public const int RecommendName = 5046;
    public const int RecommendVersion = 5047;
    public const int RecommendFlags = 5048;
    public const int Encoding = 5062;
    public const int PayloadDigest = 5092;
    public const int PayloadDigestAlgo = 5093;
    public const int PayloadDigestAlt = 5097;
}

/// <summary>Dependency flags (rpmds.h, RPMSENSE_*).</summary>
[Flags]
internal enum RpmSense
{
    Any = 0,
    Less = 2,
    Greater = 4,
    Equal = 8,
    Rpmlib = 1 << 24,
}

/// <summary>
/// Builds an rpm header structure (the signature header and the main header share it; rpm's
/// <c>docs/manual/format_header.md</c>): the magic <c>8E AD E8 01</c>, four reserved bytes, the
/// index entry count and data size, the index (tag, type, offset, count; big-endian) sorted by
/// tag, then the data store. Data is laid out in tag order, aligned to its type's size, and the
/// whole header is one immutable region: the first index entry is the region tag, whose 16 bytes
/// of data at the end of the store are a trailer entry with the negated index size, as rpmbuild
/// writes it.
/// </summary>
internal sealed class RpmHeaderBuilder
{
    private const int Int16Type = 3;
    private const int Int32Type = 4;
    private const int StringType = 6;
    private const int BinaryType = 7;
    private const int StringArrayType = 8;
    private const int I18nStringType = 9;

    private readonly SortedDictionary<int, (int Type, int Count, byte[] Data, int Alignment)> _entries = [];

    public RpmHeaderBuilder Int16(int tag, IReadOnlyList<int> values)
    {
        var data = new byte[values.Count * 2];
        for (var i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(i * 2), (ushort)values[i]);
        }

        return Add(tag, Int16Type, values.Count, data, 2);
    }

    public RpmHeaderBuilder Int32(int tag, params IReadOnlyList<long> values)
    {
        var data = new byte[values.Count * 4];
        for (var i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(i * 4), checked((uint)values[i]));
        }

        return Add(tag, Int32Type, values.Count, data, 4);
    }

    public RpmHeaderBuilder String(int tag, string value) => Add(tag, StringType, 1, Strings([value]), 1);

    public RpmHeaderBuilder I18nString(int tag, string value) => Add(tag, I18nStringType, 1, Strings([value]), 1);

    public RpmHeaderBuilder StringArray(int tag, IReadOnlyList<string> values) => Add(tag, StringArrayType, values.Count, Strings(values), 1);

    public RpmHeaderBuilder Binary(int tag, byte[] value) => Add(tag, BinaryType, value.Length, value, 1);

    private RpmHeaderBuilder Add(int tag, int type, int count, byte[] data, int alignment)
    {
        if (count == 0)
        {
            throw new ArgumentException($"Tag {tag} has no values; rpm does not allow empty entries.", nameof(count));
        }

        _entries.Add(tag, (type, count, data, alignment));
        return this;
    }

    private static byte[] Strings(IReadOnlyList<string> values)
    {
        var output = new MemoryStream();
        foreach (var value in values)
        {
            if (value.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("rpm header strings cannot contain NUL characters.", nameof(values));
            }

            output.Write(Encoding.UTF8.GetBytes(value));
            output.WriteByte(0);
        }

        return output.ToArray();
    }

    /// <summary>Returns the header bytes with <paramref name="regionTag"/> as its immutable region.</summary>
    public byte[] Build(int regionTag)
    {
        var count = _entries.Count + 1;
        var index = new byte[count * 16];
        var store = new MemoryStream();
        var i = 1;
        foreach (var (tag, (type, entryCount, data, alignment)) in _entries)
        {
            while (store.Length % alignment != 0)
            {
                store.WriteByte(0);
            }

            WriteEntry(index.AsSpan(i++ * 16), tag, type, (int)store.Length, entryCount);
            store.Write(data);
        }

        var trailerOffset = (int)store.Length;
        var trailer = new byte[16];
        WriteEntry(trailer, regionTag, BinaryType, -(count * 16), 16);
        store.Write(trailer);
        WriteEntry(index, regionTag, BinaryType, trailerOffset, 16);

        var header = new byte[16 + index.Length + store.Length];
        header[0] = 0x8E;
        header[1] = 0xAD;
        header[2] = 0xE8;
        header[3] = 0x01;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), count);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12), (int)store.Length);
        index.CopyTo(header, 16);
        store.ToArray().CopyTo(header, 16 + index.Length);
        return header;
    }

    private static void WriteEntry(Span<byte> destination, int tag, int type, int offset, int count)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination, tag);
        BinaryPrimitives.WriteInt32BigEndian(destination[4..], type);
        BinaryPrimitives.WriteInt32BigEndian(destination[8..], offset);
        BinaryPrimitives.WriteInt32BigEndian(destination[12..], count);
    }
}
