using System.Buffers.Binary;
using System.Formats.Asn1;

namespace Zexoro.Signing.Tests;

/// <summary>Temporary files and helpers for reading signed images back.</summary>
internal sealed class TempFile : IDisposable
{
    public TempFile(byte[] contents)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"zexoro-signing-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(Path, contents);
    }

    public string Path { get; }

    public byte[] ReadAllBytes() => File.ReadAllBytes(Path);

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
        }
    }
}

internal static class SignedImage
{
    /// <summary>The certificate table entry of a PE image: (offset, size).</summary>
    public static (int Offset, int Size) CertificateTable(byte[] image, bool pe32Plus)
    {
        var entry = Fixtures.TestPe.CertificateEntryOffset(pe32Plus);
        return (
            BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(entry)),
            BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(entry + 4)));
    }

    /// <summary>The PKCS #7 blob in the (single) WIN_CERTIFICATE of a signed image.</summary>
    public static byte[] SignedData(byte[] image, bool pe32Plus)
    {
        var (offset, size) = CertificateTable(image, pe32Plus);
        var length = BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(offset));
        Assert.Equal(size, length);
        Assert.Equal(0x0200, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset + 4)));
        Assert.Equal(0x0002, BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset + 6)));

        // The DER value tells its own length; anything after it is alignment padding.
        var blob = image.AsSpan(offset + 8, length - 8);
        AsnDecoder.ReadEncodedValue(blob, AsnEncodingRules.DER, out _, out _, out var consumed);
        Assert.True(blob[consumed..].IndexOfAnyExcept((byte)0) < 0, "padding after the signature must be zero");
        Assert.True(length - 8 - consumed < 8, "padding must be shorter than 8 bytes");
        return blob[..consumed].ToArray();
    }

    public static uint CheckSumField(byte[] image) =>
        BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(Fixtures.TestPe.CheckSumOffset));
}
