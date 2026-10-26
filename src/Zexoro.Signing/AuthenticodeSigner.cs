using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Zexoro.Signing;

/// <summary>Signs PE files (exe, dll) with Authenticode, without signtool.</summary>
public static class AuthenticodeSigner
{
    private const ushort CertificateRevision2 = 0x0200;
    private const ushort CertificateTypePkcsSignedData = 0x0002;
    private const int WinCertificateHeaderSize = 8;

    /// <summary>
    /// Signs the PE file at <paramref name="path"/> in place with a SHA-256 Authenticode signature.
    /// An existing signature is replaced.
    /// </summary>
    /// <exception cref="SigningException">
    /// The file is not a supported PE file, or the timestamp authority could not be used. The file
    /// is left unchanged in these cases.
    /// </exception>
    public static void SignFile(string path, SigningOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);

        using var stream = OpenFile(path, FileAccess.ReadWrite, FileShare.None);
        var image = PeImage.Read(stream);

        // Everything that can fail (digest, signing, timestamp) happens before the file changes.
        var digest = AuthenticodeDigest.Compute(stream, image, HashAlgorithmName.SHA256);
        Func<byte[], byte[]?>? timestamp = options.TimestampUrl is { } url
            ? signature => TimestampClient.GetToken(url, signature, options.HttpClient, options.TimestampTimeout)
            : null;
        var signedData = AuthenticodeSignedData.Build(
            digest, options.Credential, options.Description, options.DescriptionUrl, timestamp);

        WriteSignature(stream, image, signedData);
    }

    /// <summary>Whether the PE file at <paramref name="path"/> carries an Authenticode signature.</summary>
    /// <exception cref="SigningException">The file is not a supported PE file.</exception>
    public static bool IsSigned(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var stream = OpenFile(path, FileAccess.Read, FileShare.Read);
        var image = PeImage.Read(stream);
        if (!image.HasCertificateTable || image.CertificateTableSize < WinCertificateHeaderSize)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[WinCertificateHeaderSize];
        PeImage.ReadAt(stream, image.CertificateTableOffset, header, "the certificate table");
        var revision = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        var type = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        return revision == CertificateRevision2 && type == CertificateTypePkcsSignedData;
    }

    /// <summary>The SHA-256 Authenticode digest of the PE file, as it is (or would be) signed.</summary>
    internal static byte[] ComputeDigest(Stream stream) =>
        AuthenticodeDigest.Compute(stream, PeImage.Read(stream), HashAlgorithmName.SHA256);

    /// <summary>
    /// Replaces any certificate table with a single WIN_CERTIFICATE holding <paramref name="signedData"/>,
    /// updates the data directory entry and recomputes the checksum.
    /// </summary>
    internal static void WriteSignature(Stream stream, PeImage image, byte[] signedData)
    {
        // The table starts on an 8-byte boundary; the zero padding before it is covered by the digest.
        var tableOffset = image.SignedDataEnd + AuthenticodeDigest.PaddingTo8(image.SignedDataEnd);
        var entryPadding = AuthenticodeDigest.PaddingTo8(signedData.Length);
        var entryLength = WinCertificateHeaderSize + signedData.Length + entryPadding;

        var entry = new byte[entryLength];
        BinaryPrimitives.WriteInt32LittleEndian(entry, entryLength);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(4), CertificateRevision2);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(6), CertificateTypePkcsSignedData);
        signedData.CopyTo(entry, WinCertificateHeaderSize);

        if (tableOffset + entryLength > uint.MaxValue)
        {
            throw new SigningException("The signed file would exceed the 4 GiB limit of the PE format.");
        }

        stream.SetLength(image.SignedDataEnd);
        stream.Position = image.SignedDataEnd;
        stream.Write(new byte[tableOffset - image.SignedDataEnd]);
        stream.Write(entry);

        Span<byte> directoryEntry = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(directoryEntry, (uint)tableOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(directoryEntry[4..], (uint)entryLength);
        stream.Position = image.CertificateTableEntryOffset;
        stream.Write(directoryEntry);

        stream.Flush();
        var checksum = PeChecksum.Compute(stream, image.CheckSumOffset);
        Span<byte> checksumBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(checksumBytes, checksum);
        stream.Position = image.CheckSumOffset;
        stream.Write(checksumBytes);
        stream.Flush();
    }

    private static FileStream OpenFile(string path, FileAccess access, FileShare share)
    {
        try
        {
            return new FileStream(path, FileMode.Open, access, share);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SigningException($"Cannot open '{path}': {ex.Message}", ex);
        }
    }
}
