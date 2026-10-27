using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Zexoro.Formats.Linux;

/// <summary>
/// The tar segments of an apk v2 package (<c>apk-v2(5)</c>), written as abuild and
/// <c>abuild-tar</c> write them and as apk-tools 2 and 3 read them: ustar headers (magic
/// <c>ustar</c>, owner and group <c>root</c>/0, the fixed <see cref="ReproducibleTar.Timestamp"/>),
/// names without a leading <c>./</c>, directories with a trailing <c>/</c>, and before every
/// regular file and symbolic link a pax extended header (<c>x</c>) with
/// <c>APK-TOOLS.checksum.SHA1</c>: the SHA-1 of the content, or of the link target, as
/// <c>abuild-tar --hash</c> adds it. apk-tools 3 refuses package files without it. Names and link
/// targets longer than a ustar field go into the same pax header as <c>path</c> and
/// <c>linkpath</c>, which both apk-tools versions read. Each segment is compressed as a gzip stream
/// of its own whose header's OS byte is fixed to Unix (as in <see cref="ReproducibleTar"/>).
/// </summary>
internal static class ApkArchive
{
    /// <summary>The pax keyword of the per-file checksum.</summary>
    public const string ChecksumKey = "APK-TOOLS.checksum.SHA1";

    private const int BlockSize = 512;

    /// <summary>The largest file a ustar size field (11 octal digits) can describe.</summary>
    private const long MaximumSize = (1L << 33) - 1;

    private static readonly long Timestamp = ReproducibleTar.Timestamp.ToUnixTimeSeconds();

    /// <summary>
    /// Writes a "cut" segment (no end-of-archive blocks, as <c>abuild-tar --cut</c>): the
    /// signature segment or the control segment, each a list of files in memory, in order.
    /// Returns the gzip-compressed bytes.
    /// </summary>
    public static byte[] CutSegment(IEnumerable<(string Name, byte[] Content)> files)
    {
        using var buffer = new MemoryStream();
        Compress(buffer, tar =>
        {
            foreach (var (name, content) in files)
            {
                WriteHeader(tar, name, '0', TarItem.Regular, content.Length, null, []);
                tar.Write(content);
                Pad(tar, content.Length);
            }
        });
        return buffer.ToArray();
    }

    /// <summary>
    /// Writes the data segment to <paramref name="destination"/>: <paramref name="items"/> sorted by
    /// name (ordinal, as abuild sorts with <c>LC_ALL=C</c>), with checksums, followed by the two
    /// end-of-archive blocks, gzip-compressed.
    /// </summary>
    /// <exception cref="ArgumentException">A name is invalid or occurs twice.</exception>
    /// <exception cref="IOException">A file cannot be read or is too large.</exception>
    public static void WriteDataSegment(IEnumerable<TarItem> items, string destination)
    {
        var sorted = items.ToList();
        sorted.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        for (var i = 0; i < sorted.Count; i++)
        {
            var name = sorted[i].Name;
            if (name.Length == 0 || name.StartsWith('/') || name.EndsWith('/') || name.Contains('\\', StringComparison.Ordinal)
                || name.Split('/').Any(s => s.Length == 0 || s is "." or ".."))
            {
                throw new ArgumentException($"'{name}' is not a valid package path.", nameof(items));
            }

            if (i > 0 && sorted[i - 1].Name == name)
            {
                throw new ArgumentException($"'{name}' occurs twice.", nameof(items));
            }
        }

        using var file = new FileStream(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Compress(file, tar =>
        {
            foreach (var item in sorted)
            {
                WriteItem(tar, item);
            }

            tar.Write(new byte[2 * BlockSize]);
        });
    }

    private static void WriteItem(Stream tar, TarItem item)
    {
        switch (item.Kind)
        {
            case TarItemKind.Directory:
                WriteHeader(tar, item.Name + "/", '5', item.Mode, 0, null, []);
                break;
            case TarItemKind.SymbolicLink:
                var target = item.LinkTarget ?? throw new ArgumentException($"The link '{item.Name}' has no target.", nameof(item));
                WriteHeader(tar, item.Name, '2', item.Mode, 0, target, [(ChecksumKey, Sha1Hex(Encoding.UTF8.GetBytes(target)))]);
                break;
            case TarItemKind.File when item.SourcePath is { } path:
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var length = source.Length;
                    var hash = Convert.ToHexStringLower(SHA1Hash(source));
                    source.Position = 0;
                    WriteHeader(tar, item.Name, '0', item.Mode, length, null, [(ChecksumKey, hash)]);
                    var copied = CopyExactly(source, tar, length);
                    if (copied != length)
                    {
                        throw new IOException($"{path} changed while it was being packed.");
                    }

                    Pad(tar, length);
                }

                break;
            default:
                var content = item.Content ?? [];
                WriteHeader(tar, item.Name, '0', item.Mode, content.Length, null, [(ChecksumKey, Sha1Hex(content))]);
                tar.Write(content);
                Pad(tar, content.Length);
                break;
        }
    }

#pragma warning disable CA5350 // apk's per-file checksums are defined as SHA-1 (abuild-tar, apk-tools); package integrity rests on the SHA-256 datahash and the signature.
    private static byte[] SHA1Hash(Stream stream) => SHA1.HashData(stream);

    private static string Sha1Hex(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));
#pragma warning restore CA5350

    private static long CopyExactly(Stream source, Stream destination, long length)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (total < length)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, length - total));
            if (read == 0)
            {
                break;
            }

            destination.Write(buffer, 0, read);
            total += read;
        }

        return total;
    }

    /// <summary>Compresses what <paramref name="write"/> writes as one gzip stream, with the OS byte fixed.</summary>
    private static void Compress(Stream output, Action<Stream> write)
    {
        var start = output.Position;
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            write(gzip);
        }

        var end = output.Position;
        output.Position = start + ReproducibleTar.GzipOsOffset;
        output.WriteByte(ReproducibleTar.GzipOsUnix);
        output.Position = end;
    }

    /// <summary>
    /// Writes the header of one entry, preceded by a pax header when <paramref name="pax"/> has
    /// records or the name or link target does not fit its ustar field.
    /// </summary>
    internal static void WriteHeader(Stream tar, string name, char type, UnixFileMode mode, long size, string? linkTarget, IReadOnlyList<(string Key, string Value)> pax)
    {
        if (size > MaximumSize)
        {
            throw new IOException($"'{name}' is larger than 8 GiB, which apk packages cannot hold.");
        }

        var records = new List<(string Key, string Value)>();
        var nameBytes = Encoding.UTF8.GetBytes(name);
        if (nameBytes.Length > 100)
        {
            records.Add(("path", name));
        }

        var linkBytes = linkTarget is null ? [] : Encoding.UTF8.GetBytes(linkTarget);
        if (linkBytes.Length > 100)
        {
            records.Add(("linkpath", linkTarget!));
        }

        records.AddRange(pax);
        if (records.Count > 0)
        {
            var data = PaxData(records);
            var baseName = name.TrimEnd('/');
            baseName = baseName[(baseName.LastIndexOf('/') + 1)..];
            tar.Write(Header(Truncate(Encoding.UTF8.GetBytes("PaxHeaders/" + baseName)), 'x', TarItem.Regular, data.Length, []));
            tar.Write(data);
            Pad(tar, data.Length);
        }

        tar.Write(Header(Truncate(nameBytes), type, mode, size, Truncate(linkBytes)));
    }

    /// <summary>Pax records, <c>"&lt;length&gt; &lt;key&gt;=&lt;value&gt;\n"</c>, the length counting itself.</summary>
    internal static byte[] PaxData(IEnumerable<(string Key, string Value)> records)
    {
        var text = new StringBuilder();
        foreach (var (key, value) in records)
        {
            var body = Encoding.UTF8.GetByteCount($" {key}={value}\n");
            var length = body + body.ToString(CultureInfo.InvariantCulture).Length;
            if (length.ToString(CultureInfo.InvariantCulture).Length != (length - body))
            {
                length++;
            }

            text.Append(length.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(key).Append('=').Append(value).Append('\n');
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static byte[] Truncate(byte[] bytes) => bytes.Length > 100 ? bytes[..100] : bytes;

    private static byte[] Header(byte[] name, char type, UnixFileMode mode, long size, byte[] link)
    {
        var header = new byte[BlockSize];
        name.CopyTo(header, 0);
        Octal(header, 100, 8, (long)mode);
        Octal(header, 108, 8, 0);
        Octal(header, 116, 8, 0);
        Octal(header, 124, 12, size);
        Octal(header, 136, 12, Timestamp);
        header[156] = (byte)type;
        link.CopyTo(header, 157);
        "ustar\0"u8.CopyTo(header.AsSpan(257));
        "00"u8.CopyTo(header.AsSpan(263));
        "root"u8.CopyTo(header.AsSpan(265));
        "root"u8.CopyTo(header.AsSpan(297));
        Octal(header, 329, 8, 0);
        Octal(header, 337, 8, 0);
        header.AsSpan(148, 8).Fill((byte)' ');
        var sum = 0;
        foreach (var b in header)
        {
            sum += b;
        }

        // "%06o\0 ", as GNU tar writes the checksum.
        Encoding.ASCII.GetBytes(Convert.ToString(sum, 8).PadLeft(6, '0')).CopyTo(header, 148);
        header[154] = 0;
        header[155] = (byte)' ';
        return header;
    }

    /// <summary>Writes <paramref name="value"/> as zero-padded octal digits followed by a NUL.</summary>
    private static void Octal(byte[] header, int offset, int length, long value)
    {
        var digits = Convert.ToString(value, 8).PadLeft(length - 1, '0');
        Encoding.ASCII.GetBytes(digits).CopyTo(header, offset);
        header[offset + length - 1] = 0;
    }

    private static void Pad(Stream tar, long length)
    {
        var remainder = (int)(length % BlockSize);
        if (remainder != 0)
        {
            tar.Write(new byte[BlockSize - remainder]);
        }
    }
}
