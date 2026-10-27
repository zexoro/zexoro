using System.Buffers.Binary;
using System.IO.Compression;

namespace Zexoro.Formats.Linux;

/// <summary>An icon file is not a usable PNG or ICO file; the message is for the user.</summary>
public sealed class IconException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>A PNG icon and its size in pixels.</summary>
public sealed record PngImage(byte[] Bytes, int Width, int Height);

/// <summary>
/// PNG icons for Linux formats (docs/design/0006-desktop-formats.md section 3): a PNG file is used
/// as it is; from an ICO file the largest image is taken, as stored when it is a PNG and re-encoded
/// as PNG when it is a bitmap (1, 4, 8, 24 or 32 bits per pixel, with the AND mask as transparency).
/// </summary>
public static class PngIcon
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Reads the PNG or ICO file at <paramref name="path"/> and returns a PNG image.</summary>
    /// <exception cref="IconException">The file is neither a valid PNG nor a valid ICO file.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static PngImage Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return FromBytes(File.ReadAllBytes(path));
    }

    /// <summary>Returns a PNG image from the bytes of a PNG or ICO file.</summary>
    /// <exception cref="IconException">The bytes are neither a valid PNG nor a valid ICO file.</exception>
    public static PngImage FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (IsPng(bytes))
        {
            var (width, height) = ReadPngSize(bytes);
            return new PngImage(bytes, width, height);
        }

        if (bytes.Length >= 6 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0)
        {
            return FromIco(bytes);
        }

        throw new IconException("not a PNG or ICO file");
    }

    /// <summary>Whether <paramref name="bytes"/> start with the PNG signature.</summary>
    public static bool IsPng(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Signature);

    private static (int Width, int Height) ReadPngSize(byte[] bytes)
    {
        // The signature is followed by the IHDR chunk: length 13, "IHDR", width, height.
        if (bytes.Length < 33 || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8)) != 13 || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
        {
            throw new IconException("a PNG image without a valid IHDR chunk");
        }

        var width = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16));
        var height = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20));
        if (width <= 0 || height <= 0)
        {
            throw new IconException($"a PNG image with an invalid size {width}x{height}");
        }

        return (width, height);
    }

    private static PngImage FromIco(byte[] bytes)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4));
        if (count == 0 || bytes.Length < 6 + (count * 16))
        {
            throw new IconException("an ICO file without images or with a truncated directory");
        }

        PngImage? best = null;
        var bestDepth = -1;
        for (var i = 0; i < count; i++)
        {
            var entry = bytes.AsSpan(6 + (i * 16), 16);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if (offset > (uint)bytes.Length || size > (uint)bytes.Length - offset)
            {
                throw new IconException($"image {i + 1} of the ICO file lies outside the file");
            }

            var data = bytes.AsSpan((int)offset, (int)size);
            PngImage image;
            int depth;
            if (IsPng(data))
            {
                var png = data.ToArray();
                var (width, height) = ReadPngSize(png);
                image = new PngImage(png, width, height);
                depth = 32;
            }
            else
            {
                (image, depth) = DecodeBitmap(data, i + 1);
            }

            var area = (long)image.Width * image.Height;
            var bestArea = best is null ? -1 : (long)best.Width * best.Height;
            if (area > bestArea || (area == bestArea && depth > bestDepth))
            {
                best = image;
                bestDepth = depth;
            }
        }

        return best!;
    }

    /// <summary>Decodes an ICO bitmap (BITMAPINFOHEADER, XOR image, AND mask) and encodes it as PNG.</summary>
    private static (PngImage Image, int Depth) DecodeBitmap(ReadOnlySpan<byte> data, int number)
    {
        IconException Invalid(string what) => new($"image {number} of the ICO file {what}");
        if (data.Length < 40 || BinaryPrimitives.ReadInt32LittleEndian(data) < 40)
        {
            throw Invalid("is neither a PNG nor a bitmap");
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(data);
        var width = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        var height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(data[8..])) / 2;
        var depth = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(data[32..]);
        if (width is <= 0 or > 1024 || height is <= 0 or > 1024)
        {
            throw Invalid($"has an unsupported size {width}x{height}");
        }

        if (depth is not (1 or 4 or 8 or 24 or 32) || compression is not (0 or 3) || (compression == 3 && depth != 32))
        {
            throw Invalid($"is a bitmap with {depth} bits per pixel and compression {compression}, which zexoro cannot convert; use a PNG icon ([linux] icon)");
        }

        var paletteSize = depth <= 8 ? (int)(colorsUsed is > 0 and <= 256 ? colorsUsed : 1u << depth) : 0;
        var paletteOffset = headerSize + (compression == 3 && headerSize == 40 ? 12 : 0);
        var pixelsOffset = paletteOffset + (paletteSize * 4);
        var stride = ((width * depth) + 31) / 32 * 4;
        var maskStride = (width + 31) / 32 * 4;
        var maskOffset = pixelsOffset + (stride * height);
        var hasMask = data.Length >= maskOffset + (maskStride * height);
        if (data.Length < maskOffset)
        {
            throw Invalid("is truncated");
        }

        var rgba = new byte[width * height * 4];
        var anyAlpha = false;
        for (var y = 0; y < height; y++)
        {
            // Bitmap rows are stored bottom-up.
            var row = data.Slice(pixelsOffset + ((height - 1 - y) * stride), stride);
            for (var x = 0; x < width; x++)
            {
                var target = ((y * width) + x) * 4;
                byte b, g, r, a = 255;
                if (depth >= 24)
                {
                    var pixel = row[(x * depth / 8)..];
                    (b, g, r) = (pixel[0], pixel[1], pixel[2]);
                    if (depth == 32)
                    {
                        a = pixel[3];
                        anyAlpha |= a != 0;
                    }
                }
                else
                {
                    var bit = x * depth;
                    var index = (row[bit / 8] >> (8 - depth - (bit % 8))) & ((1 << depth) - 1);
                    if (index >= paletteSize)
                    {
                        throw Invalid("refers to a color outside its palette");
                    }

                    var color = data.Slice(paletteOffset + (index * 4), 4);
                    (b, g, r) = (color[0], color[1], color[2]);
                }

                rgba[target] = r;
                rgba[target + 1] = g;
                rgba[target + 2] = b;
                rgba[target + 3] = a;
            }
        }

        // The AND mask is the transparency of bitmaps without an alpha channel (and of 32-bit
        // bitmaps whose alpha is all zero, as old icon editors wrote them).
        if (hasMask && (depth < 32 || !anyAlpha))
        {
            for (var y = 0; y < height; y++)
            {
                var row = data.Slice(maskOffset + ((height - 1 - y) * maskStride), maskStride);
                for (var x = 0; x < width; x++)
                {
                    var transparent = ((row[x / 8] >> (7 - (x % 8))) & 1) == 1;
                    rgba[(((y * width) + x) * 4) + 3] = transparent ? (byte)0 : (byte)255;
                }
            }
        }

        return (new PngImage(Encode(width, height, rgba), width, height), depth);
    }

    /// <summary>Encodes 8-bit RGBA pixels (rows top-down) as a PNG file.</summary>
    internal static byte[] Encode(int width, int height, byte[] rgba)
    {
        using var output = new MemoryStream();
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        WriteChunk(output, "IHDR"u8, header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0); // filter: none
                zlib.Write(rgba, y * width * 4, width * 4);
            }
        }

        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);
        var crc = Crc(Crc(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
