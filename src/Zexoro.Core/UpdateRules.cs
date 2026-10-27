using System.Diagnostics.CodeAnalysis;
using System.Formats.Asn1;
using System.Security.Cryptography;

namespace Zexoro;

/// <summary>
/// Value rules of the update contract (docs/design/0002-update-contract.md) that both the
/// configuration (<c>[updates]</c>) and the update library (<c>Zexoro.Updates</c>) apply, kept in
/// one place so they cannot drift apart.
/// </summary>
public static class UpdateRules
{
    /// <summary>The default channel when <c>[updates] channel</c> is not set.</summary>
    public const string DefaultChannel = "stable";

    private const string EcPublicKeyOid = "1.2.840.10045.2.1";
    private const string P256Oid = "1.2.840.10045.3.1.7";

    /// <summary>Whether <paramref name="url"/> is an absolute https URL, or an http URL on localhost, 127.0.0.1 or [::1].</summary>
    public static bool IsAllowedUrl(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri)
        {
            return false;
        }

        if (url.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }

        return url.Scheme == Uri.UriSchemeHttp
            && (string.Equals(url.IdnHost, "localhost", StringComparison.OrdinalIgnoreCase)
                || url.Host is "127.0.0.1" or "[::1]");
    }

    /// <summary>A channel names a feed and appears in URLs: ASCII letters, digits, '.', '-' and '_', not starting with '.'.</summary>
    public static bool IsValidChannel(string? channel) =>
        channel is { Length: > 0 } && channel.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') && channel[0] != '.';

    /// <summary>
    /// Checks the base64 text of an update public key: a DER SubjectPublicKeyInfo of an ECDSA P-256
    /// key. Surrounding whitespace is ignored.
    /// </summary>
    /// <param name="text">The key text.</param>
    /// <param name="subjectPublicKeyInfo">The decoded key, when valid.</param>
    /// <param name="error">Why it is not valid.</param>
    public static bool TryDecodePublicKey(
        string? text,
        [NotNullWhen(true)] out byte[]? subjectPublicKeyInfo,
        [NotNullWhen(false)] out string? error)
    {
        subjectPublicKeyInfo = null;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(text?.Trim() ?? string.Empty);
        }
        catch (FormatException)
        {
            error = "expected base64 text";
            return false;
        }

        if (bytes.Length == 0)
        {
            error = "the key is empty";
            return false;
        }

        if (!IsP256SubjectPublicKeyInfo(bytes, out error))
        {
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length)
            {
                error = "unexpected data after the key";
                return false;
            }
        }
        catch (CryptographicException ex)
        {
            error = ex.Message;
            return false;
        }

        subjectPublicKeyInfo = bytes;
        return true;
    }

    /// <summary>Checks that <paramref name="spki"/> is a SubjectPublicKeyInfo for id-ecPublicKey on the P-256 curve.</summary>
    public static bool IsP256SubjectPublicKeyInfo(byte[] spki, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(spki);
        try
        {
            var reader = new AsnReader(spki, AsnEncodingRules.DER);
            var info = reader.ReadSequence();
            reader.ThrowIfNotEmpty();
            var algorithm = info.ReadSequence();
            var algorithmOid = algorithm.ReadObjectIdentifier();
            if (algorithmOid != EcPublicKeyOid)
            {
                error = "not an elliptic curve key";
                return false;
            }

            if (algorithm.PeekTag() != Asn1Tag.ObjectIdentifier || algorithm.ReadObjectIdentifier() != P256Oid)
            {
                error = "not a P-256 key";
                return false;
            }

            algorithm.ThrowIfNotEmpty();
            info.ReadBitString(out _);
            info.ThrowIfNotEmpty();
        }
        catch (AsnContentException)
        {
            error = "not a DER SubjectPublicKeyInfo";
            return false;
        }

        error = null;
        return true;
    }
}
