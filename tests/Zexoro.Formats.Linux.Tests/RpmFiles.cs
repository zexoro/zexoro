using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Zexoro.Formats.Linux.Tests;

/// <summary>
/// An independent reader of rpm files for the tests, written from rpm's format documentation: it
/// checks the lead, both headers' structure (magic, sorted index, alignment, the region trailer)
/// and decompresses the cpio payload.
/// </summary>
internal sealed class RpmFiles
{
    public required byte[] Lead { get; init; }

    public required Header Signature { get; init; }

    public required Header Main { get; init; }

    /// <summary>The bytes of the main header as stored (what digests and the signature cover).</summary>
    public required byte[] MainBytes { get; init; }

    /// <summary>The compressed payload as stored.</summary>
    public required byte[] Payload { get; init; }

    public required byte[] Cpio { get; init; }

    public required List<CpioEntry> Entries { get; init; }

    public sealed record CpioEntry(string Name, int Inode, int Mode, int Uid, int Gid, int Links, long Mtime, byte[] Data);

    public sealed class Header
    {
        public Dictionary<int, (int Type, int Count, object Value)> Tags { get; } = [];

        public int RegionTag { get; set; }

        public string String(int tag) => (string)Tags[tag].Value;

        public string[] Strings(int tag) => (string[])Tags[tag].Value;

        public long[] Numbers(int tag) => (long[])Tags[tag].Value;

        public byte[] Binary(int tag) => (byte[])Tags[tag].Value;
    }

    public static RpmFiles Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal([0xED, 0xAB, 0xEE, 0xDB, 3, 0], bytes[..6]);
        Assert.Equal(5, BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(78)));
        var position = 96;
        var signature = ReadHeader(bytes, ref position, 62);
        while (position % 8 != 0)
        {
            Assert.Equal(0, bytes[position++]);
        }

        var mainStart = position;
        var main = ReadHeader(bytes, ref position, 63);
        var payload = bytes[position..];
        using var gzip = new GZipStream(new MemoryStream(payload), CompressionMode.Decompress);
        using var cpio = new MemoryStream();
        gzip.CopyTo(cpio);
        var archive = cpio.ToArray();
        return new RpmFiles
        {
            Lead = bytes[..96],
            Signature = signature,
            Main = main,
            MainBytes = bytes[mainStart..position],
            Payload = payload,
            Cpio = archive,
            Entries = ReadCpio(archive),
        };
    }

    private static Header ReadHeader(byte[] bytes, ref int position, int regionTag)
    {
        Assert.Equal([0x8E, 0xAD, 0xE8, 0x01, 0, 0, 0, 0], bytes[position..(position + 8)]);
        var count = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(position + 8));
        var size = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(position + 12));
        var index = position + 16;
        var store = bytes[(index + (count * 16))..(index + (count * 16) + size)];
        var header = new Header();
        var previousTag = -1;
        var previousEnd = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = bytes.AsSpan(index + (i * 16));
            var tag = BinaryPrimitives.ReadInt32BigEndian(entry);
            var type = BinaryPrimitives.ReadInt32BigEndian(entry[4..]);
            var offset = BinaryPrimitives.ReadInt32BigEndian(entry[8..]);
            var n = BinaryPrimitives.ReadInt32BigEndian(entry[12..]);
            if (i == 0)
            {
                // The region tag: its data is the last 16 bytes, an entry with the negated index size.
                Assert.Equal(regionTag, tag);
                Assert.Equal(7, type);
                Assert.Equal(16, n);
                Assert.Equal(size - 16, offset);
                var trailer = store.AsSpan(offset);
                Assert.Equal(regionTag, BinaryPrimitives.ReadInt32BigEndian(trailer));
                Assert.Equal(7, BinaryPrimitives.ReadInt32BigEndian(trailer[4..]));
                Assert.Equal(-(count * 16), BinaryPrimitives.ReadInt32BigEndian(trailer[8..]));
                Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(trailer[12..]));
                header.RegionTag = tag;
                continue;
            }

            Assert.True(tag > previousTag, $"tag {tag} is out of order");
            Assert.True(offset >= previousEnd, $"tag {tag} overlaps the previous one");
            previousTag = tag;
            var alignment = type switch { 3 => 2, 4 => 4, 5 => 8, _ => 1 };
            Assert.Equal(0, offset % alignment);
            object value;
            int end;
            switch (type)
            {
                case 3:
                    value = Enumerable.Range(0, n).Select(k => (long)BinaryPrimitives.ReadUInt16BigEndian(store.AsSpan(offset + (k * 2)))).ToArray();
                    end = offset + (n * 2);
                    break;
                case 4:
                    value = Enumerable.Range(0, n).Select(k => (long)BinaryPrimitives.ReadUInt32BigEndian(store.AsSpan(offset + (k * 4)))).ToArray();
                    end = offset + (n * 4);
                    break;
                case 7:
                    value = store[offset..(offset + n)];
                    end = offset + n;
                    break;
                case 6 or 8 or 9:
                    var strings = new string[n];
                    var at = offset;
                    for (var k = 0; k < n; k++)
                    {
                        var zero = Array.IndexOf(store, (byte)0, at) - at;
                        strings[k] = Encoding.UTF8.GetString(store, at, zero);
                        at += zero + 1;
                    }

                    Assert.True(type != 6 || n == 1);
                    value = type == 6 ? strings[0] : strings;
                    end = at;
                    break;
                default:
                    throw new InvalidDataException($"unexpected type {type}");
            }

            Assert.True(end <= size - 16, $"tag {tag} runs into the region trailer");
            previousEnd = end;
            header.Tags.Add(tag, (type, n, value));
        }

        position = index + (count * 16) + size;
        return header;
    }

    private static List<CpioEntry> ReadCpio(byte[] archive)
    {
        var entries = new List<CpioEntry>();
        var position = 0;
        while (true)
        {
            var text = Encoding.ASCII.GetString(archive, position, 110);
            Assert.StartsWith("070701", text, StringComparison.Ordinal);
            int Field(int i) => int.Parse(text.AsSpan(6 + (i * 8), 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var nameSize = Field(11);
            var name = Encoding.UTF8.GetString(archive, position + 110, nameSize - 1);
            Assert.Equal(0, archive[position + 110 + nameSize - 1]);
            position += 110 + nameSize;
            position = (position + 3) & ~3;
            if (name == "TRAILER!!!")
            {
                Assert.Equal(archive.Length, position);
                return entries;
            }

            var size = Field(6);
            entries.Add(new CpioEntry(name, Field(0), Field(1), Field(2), Field(3), Field(4), Field(5), archive[position..(position + size)]));
            position = (position + size + 3) & ~3;
        }
    }
}
