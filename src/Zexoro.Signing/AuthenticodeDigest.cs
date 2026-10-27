using System.Security.Cryptography;

namespace Zexoro.Signing;

/// <summary>Computes the Authenticode image digest of a PE file.</summary>
/// <remarks>
/// The digest covers every byte of the file up to the certificate table except two fields:
/// the optional header <c>CheckSum</c> and the certificate table data directory entry. When the
/// file has no certificate table and its length is not a multiple of 8, the zero bytes that
/// signing appends before the table are hashed too, so the digest of an unsigned file equals the
/// digest that is signed.
/// <para>
/// The Authenticode specification describes the same coverage section by section (headers,
/// sections ordered by <c>PointerToRawData</c>, then the remaining data). For images whose
/// sections follow each other without gaps, which is what linkers produce, both readings hash
/// identical bytes. Hashing the file linearly is what <c>signtool</c> and <c>osslsigncode</c>
/// verify in practice; it also covers gaps instead of leaving them unsigned.
/// </para>
/// </remarks>
internal static class AuthenticodeDigest
{
    private const int BufferSize = 64 * 1024;

    public static byte[] Compute(Stream stream, PeImage image, HashAlgorithmName hashAlgorithm)
    {
        using var hash = IncrementalHash.CreateHash(hashAlgorithm);
        var buffer = new byte[BufferSize];

        HashRange(stream, hash, buffer, 0, image.CheckSumOffset);
        HashRange(stream, hash, buffer, image.CheckSumOffset + 4, image.CertificateTableEntryOffset);
        HashRange(stream, hash, buffer, image.CertificateTableEntryOffset + 8, image.SignedDataEnd);

        var padding = PaddingTo8(image.SignedDataEnd);
        if (padding != 0)
        {
            hash.AppendData(new byte[padding]);
        }

        return hash.GetHashAndReset();
    }

    internal static int PaddingTo8(long length) => (int)((8 - (length % 8)) % 8);

    private static void HashRange(Stream stream, IncrementalHash hash, byte[] buffer, long start, long end)
    {
        stream.Position = start;
        var remaining = end - start;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new SigningException("Not a valid PE file: it ended while computing its digest.");
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
    }
}
