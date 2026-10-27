using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;

namespace Zexoro.Formats.Linux.Tests;

/// <summary>Reads apk v2 packages back, independently of the writer: gzip members and tar entries.</summary>
internal static class ApkFiles
{
    /// <summary>One tar entry as read back by <see cref="TarReader"/>, with its pax extended attributes.</summary>
    internal sealed record Entry(string Name, TarEntryType Type, UnixFileMode Mode, int Uid, int Gid, string UserName, string GroupName, DateTimeOffset Modified, string LinkName, byte[] Content, IReadOnlyDictionary<string, string> Pax);

    /// <summary>
    /// Splits the package into its gzip members: every offset where a gzip header as zexoro writes
    /// it starts (<c>1f 8b 08 00</c>, mtime 0, OS 3) and the bytes before the next one form a member
    /// that decompresses to content matching its CRC-32 and size trailer.
    /// </summary>
    public static List<byte[]> Segments(byte[] package)
    {
        var starts = new List<int>();
        for (var i = 0; i + 10 <= package.Length; i++)
        {
            if (package[i] == 0x1f && package[i + 1] == 0x8b && package[i + 2] == 8 && package[i + 3] == 0
                && BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(i + 4)) == 0 && package[i + 9] == 3)
            {
                starts.Add(i);
            }
        }

        var segments = new List<byte[]>();
        var start = 0;
        Assert.Equal(0, starts[0]);
        for (var k = 1; k <= starts.Count; k++)
        {
            var end = k < starts.Count ? starts[k] : package.Length;
            var candidate = package[start..end];
            if (k < starts.Count && !IsMember(candidate))
            {
                continue;
            }

            Assert.True(IsMember(candidate), "the last gzip member is not valid");
            segments.Add(candidate);
            start = end;
        }

        return segments;
    }

    private static bool IsMember(byte[] member)
    {
        try
        {
            var content = Decompress(member);
            return member.Length >= 18
                && BinaryPrimitives.ReadUInt32LittleEndian(member.AsSpan(member.Length - 8)) == Crc32(content)
                && BinaryPrimitives.ReadUInt32LittleEndian(member.AsSpan(member.Length - 4)) == (uint)content.Length;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>CRC-32 as gzip uses it (IEEE 802.3, reflected).</summary>
    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    public static byte[] Decompress(byte[] member)
    {
        using var input = new MemoryStream(member);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>The entries of the tar formed by the decompressed members, in order.</summary>
    public static List<Entry> Entries(byte[] tar)
    {
        using var stream = new MemoryStream(tar);
        using var reader = new TarReader(stream);
        var entries = new List<Entry>();
        while (reader.GetNextEntry(copyData: true) is { } entry)
        {
            var posix = (PosixTarEntry)entry;
            using var content = new MemoryStream();
            entry.DataStream?.CopyTo(content);
            var pax = entry is PaxTarEntry paxEntry ? paxEntry.ExtendedAttributes : new Dictionary<string, string>();
            entries.Add(new Entry(entry.Name, entry.EntryType, entry.Mode, entry.Uid, entry.Gid, posix.UserName, posix.GroupName, entry.ModificationTime, entry.LinkName, content.ToArray(), pax));
        }

        return entries;
    }

    /// <summary>The <c>key = value</c> lines of a <c>.PKGINFO</c>, in order.</summary>
    public static List<(string Key, string Value)> PackageInfo(byte[] content) =>
        System.Text.Encoding.UTF8.GetString(content).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(" = ", 2))
            .Select(p => (p[0], p[1]))
            .ToList();
}
