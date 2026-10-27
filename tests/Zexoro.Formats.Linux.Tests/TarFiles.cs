using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;

namespace Zexoro.Formats.Linux.Tests;

/// <summary>Reads archives back, and builds small test inputs.</summary>
internal static class TarFiles
{
    /// <summary>One entry as read back: name, type, mode, owner, times, link target and content.</summary>
    internal sealed record Entry(string Name, TarEntryType Type, UnixFileMode Mode, int Uid, int Gid, string UserName, string GroupName, DateTimeOffset Modified, string LinkName, byte[] Content);

    public static List<Entry> Read(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var entries = new List<Entry>();
        while (reader.GetNextEntry(copyData: true) is { } entry)
        {
            var posix = (PosixTarEntry)entry;
            using var content = new MemoryStream();
            entry.DataStream?.CopyTo(content);
            entries.Add(new Entry(entry.Name, entry.EntryType, entry.Mode, entry.Uid, entry.Gid, posix.UserName, posix.GroupName, entry.ModificationTime, entry.LinkName, content.ToArray()));
        }

        return entries;
    }

    /// <summary>A 64-byte ELF header for <paramref name="machine"/> (62 x86-64, 183 AArch64), optionally followed by a PT_INTERP program header.</summary>
    public static byte[] Elf(ushort machine, string? interpreter = null)
    {
        var bytes = new byte[interpreter is null ? 64 : 64 + 56 + interpreter.Length + 1];
        "\u007FELF"u8.CopyTo(bytes);
        bytes[4] = 2;
        bytes[5] = 1;
        bytes[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), machine);
        if (interpreter is not null)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x20), 64);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x36), 56);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x38), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), 3);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64 + 0x08), 120);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64 + 0x20), (ulong)interpreter.Length + 1);
            System.Text.Encoding.ASCII.GetBytes(interpreter).CopyTo(bytes, 120);
        }

        return bytes;
    }

    /// <summary>
    /// An ICO file with one 32-bit bitmap per size: every pixel (x, y) is r = x, g = y, b = size,
    /// a = 255, except (0, 0), which is fully transparent.
    /// </summary>
    public static byte[] Ico(params int[] sizes)
    {
        var images = sizes.Select(Bitmap32).ToList();
        using var output = new MemoryStream();
        var header = new byte[6 + (16 * sizes.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)sizes.Length);
        var offset = header.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            var entry = header.AsSpan(6 + (16 * i));
            entry[0] = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            entry[1] = entry[0];
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32);
            BinaryPrimitives.WriteInt32LittleEndian(entry[8..], images[i].Length);
            BinaryPrimitives.WriteInt32LittleEndian(entry[12..], offset);
            offset += images[i].Length;
        }

        output.Write(header);
        foreach (var image in images)
        {
            output.Write(image);
        }

        return output.ToArray();
    }

    /// <summary>An ICO file whose only image is a PNG.</summary>
    public static byte[] IcoWithPng(byte[] png, int size)
    {
        var bytes = new byte[22 + png.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
        bytes[6] = (byte)size;
        bytes[7] = (byte)size;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), png.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 22);
        png.CopyTo(bytes, 22);
        return bytes;
    }

    private static byte[] Bitmap32(int size)
    {
        var maskStride = (size + 31) / 32 * 4;
        var bytes = new byte[40 + (size * size * 4) + (maskStride * size)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), size);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), size * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 32);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var row = size - 1 - y; // bottom-up
                var pixel = 40 + (((row * size) + x) * 4);
                bytes[pixel] = (byte)size;
                bytes[pixel + 1] = (byte)y;
                bytes[pixel + 2] = (byte)x;
                bytes[pixel + 3] = x == 0 && y == 0 ? (byte)0 : (byte)255;
            }
        }

        return bytes;
    }

    /// <summary>Decodes a PNG written by <see cref="PngIcon"/> (RGBA, 8 bits, filter 0 on every row) to its pixels.</summary>
    public static (int Width, int Height, byte[] Rgba) DecodePng(byte[] png)
    {
        var position = 8;
        int width = 0, height = 0;
        using var idat = new MemoryStream();
        while (position < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position));
            var type = System.Text.Encoding.ASCII.GetString(png, position + 4, 4);
            var data = png.AsSpan(position + 8, length);
            if (type == "IHDR")
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                Assert.Equal(8, data[8]);
                Assert.Equal(6, data[9]);
            }
            else if (type == "IDAT")
            {
                idat.Write(data);
            }

            position += 12 + length;
        }

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var rows = raw.ToArray();
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, rows[y * ((width * 4) + 1)]);
            Array.Copy(rows, (y * ((width * 4) + 1)) + 1, rgba, y * width * 4, width * 4);
        }

        return (width, height, rgba);
    }
}
