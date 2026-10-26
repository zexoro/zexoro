using System.Text;

namespace Zexoro.Updates;

/// <summary>
/// <c>feed.json.sig</c>: a detached ECDSA P-256 / SHA-256 signature over the exact bytes of
/// <c>feed.json</c>, stored as one line of base64 of the DER signature, so that
/// <c>base64 -d feed.json.sig | openssl dgst -sha256 -verify key.pem -signature /dev/stdin feed.json</c>
/// verifies it too.
/// </summary>
public static class FeedSignature
{
    /// <summary>The largest signature file accepted; a DER P-256 signature is at most 72 bytes.</summary>
    public const int MaxFileSize = 1024;

    /// <summary>Returns the content of the signature file for <paramref name="feed"/>.</summary>
    public static byte[] Sign(ReadOnlySpan<byte> feed, UpdateSigningKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Encoding.ASCII.GetBytes(Convert.ToBase64String(key.Sign(feed)) + "\n");
    }

    /// <summary>
    /// Returns the trusted key that signed <paramref name="feed"/>, or null when the signature file is
    /// malformed or no key in <paramref name="trustedKeys"/> verifies it.
    /// </summary>
    public static UpdatePublicKey? FindSigner(ReadOnlySpan<byte> feed, ReadOnlySpan<byte> signatureFile, IEnumerable<UpdatePublicKey> trustedKeys)
    {
        ArgumentNullException.ThrowIfNull(trustedKeys);
        if (signatureFile.Length > MaxFileSize)
        {
            return null;
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(Encoding.ASCII.GetString(signatureFile).Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        if (signature.Length == 0)
        {
            return null;
        }

        foreach (var key in trustedKeys)
        {
            if (key.Verify(feed, signature))
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>Whether a key in <paramref name="trustedKeys"/> signed <paramref name="feed"/>.</summary>
    public static bool Verify(ReadOnlySpan<byte> feed, ReadOnlySpan<byte> signatureFile, IEnumerable<UpdatePublicKey> trustedKeys) =>
        FindSigner(feed, signatureFile, trustedKeys) is not null;
}
