using System.Buffers.Binary;
using Zexoro.Formats.Linux;

namespace Zexoro.Formats.MacOS;

/// <summary>
/// Apple icon files (<c>.icns</c>) with PNG images (docs/design/0006-desktop-formats.md section 3):
/// the 8-byte header <c>icns</c> plus the file length, then one element per image, a four-character
/// type, the element length and the PNG bytes. The type says the pixel size: <c>icp4</c> 16,
/// <c>icp5</c> 32, <c>icp6</c> 64, <c>ic07</c> 128, <c>ic08</c> 256, <c>ic09</c> 512, <c>ic10</c>
/// 1024 (PNG elements are read since macOS 10.7).
/// </summary>
public static class Icns
{
    private static readonly (int Size, string Type)[] Types =
        [(16, "icp4"), (32, "icp5"), (64, "icp6"), (128, "ic07"), (256, "ic08"), (512, "ic09"), (1024, "ic10")];

    /// <summary>The square pixel sizes an image may have.</summary>
    public static IReadOnlyList<int> Sizes { get; } = Types.Select(t => t.Size).ToList();

    /// <summary>Whether <paramref name="bytes"/> are an icns file: the magic and a length that matches.</summary>
    public static bool IsIcns(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && bytes.StartsWith("icns"u8) && BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]) == (uint)bytes.Length;

    /// <summary>Returns an icns file holding <paramref name="images"/>, one per size, in ascending size.</summary>
    /// <exception cref="IconException">An image is not square, has a size no icns type has, or two have the same size.</exception>
    public static byte[] Create(IEnumerable<PngImage> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        var elements = new List<(int Size, string Type, byte[] Png)>();
        foreach (var image in images)
        {
            if (image.Width != image.Height || Array.FindIndex(Types, t => t.Size == image.Width) is var index && index < 0)
            {
                throw new IconException(
                    $"a {image.Width}x{image.Height} image, but icns images are square with {string.Join(", ", Sizes)} pixels");
            }

            if (elements.Any(e => e.Size == image.Width))
            {
                throw new IconException($"two {image.Width}x{image.Width} images");
            }

            elements.Add((image.Width, Types[index].Type, image.Bytes));
        }

        if (elements.Count == 0)
        {
            throw new IconException("no images");
        }

        elements.Sort((a, b) => a.Size.CompareTo(b.Size));
        var length = 8 + elements.Sum(e => 8 + e.Png.Length);
        var bytes = new byte[length];
        "icns"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)length);
        var position = 8;
        foreach (var (_, type, png) in elements)
        {
            System.Text.Encoding.ASCII.GetBytes(type).CopyTo(bytes, position);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(position + 4), (uint)(8 + png.Length));
            png.CopyTo(bytes, position + 8);
            position += 8 + png.Length;
        }

        return bytes;
    }
}
