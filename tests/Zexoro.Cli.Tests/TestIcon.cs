using System.Buffers.Binary;

namespace Zexoro.Cli.Tests;

/// <summary>Builds a valid 16x16 32-bit ICO file, which makensis accepts as an installer icon.</summary>
internal static class TestIcon
{
    public static byte[] Create()
    {
        const int size = 16;
        const int pixelBytes = size * size * 4;
        const int maskBytes = size * 4; // 1 bit per pixel, rows padded to 4 bytes
        const int imageBytes = 40 + pixelBytes + maskBytes;
        var icon = new byte[6 + 16 + imageBytes];
        var span = icon.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1); // type: icon
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 1); // one image

        var entry = span[6..];
        entry[0] = size;
        entry[1] = size;
        BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1); // planes
        BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32); // bits per pixel
        BinaryPrimitives.WriteInt32LittleEndian(entry[8..], imageBytes);
        BinaryPrimitives.WriteInt32LittleEndian(entry[12..], 22);

        var header = span[22..];
        BinaryPrimitives.WriteInt32LittleEndian(header, 40);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], size);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], size * 2); // XOR and AND masks
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], pixelBytes + maskBytes);
        for (var i = 0; i < pixelBytes; i += 4)
        {
            header[40 + i] = 0xC0; // blue
            header[40 + i + 1] = 0x60;
            header[40 + i + 3] = 0xFF; // opaque
        }

        return icon;
    }
}
