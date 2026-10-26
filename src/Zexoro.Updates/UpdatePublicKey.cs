using System.Diagnostics.CodeAnalysis;
using System.Formats.Asn1;
using System.Security.Cryptography;

namespace Zexoro.Updates;

/// <summary>
/// A trusted feed key: an ECDSA P-256 public key, written as base64 of its DER SubjectPublicKeyInfo
/// (docs/design/0002-update-contract.md section 5), the same text that <c>zexoro keys new</c> prints
/// and that install records list.
/// </summary>
public sealed class UpdatePublicKey : IEquatable<UpdatePublicKey>
{
    internal const string EcPublicKeyOid = "1.2.840.10045.2.1";
    internal const string P256Oid = "1.2.840.10045.3.1.7";

    private readonly byte[] _subjectPublicKeyInfo;

    private UpdatePublicKey(byte[] subjectPublicKeyInfo, string text)
    {
        _subjectPublicKeyInfo = subjectPublicKeyInfo;
        Text = text;
    }

    /// <summary>The canonical base64 text.</summary>
    public string Text { get; }

    /// <summary>Parses the base64 text of a P-256 SubjectPublicKeyInfo. Surrounding whitespace is ignored.</summary>
    /// <exception cref="UpdateException">The text is not such a key (<see cref="UpdateErrorKind.InvalidKey"/>).</exception>
    public static UpdatePublicKey Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out var key, out var error)
            ? key
            : throw new UpdateException(UpdateErrorKind.InvalidKey, $"not a valid update public key: {error}");
    }

    /// <summary>Parses the base64 text of a P-256 SubjectPublicKeyInfo.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out UpdatePublicKey? key) => TryParse(text, out key, out _);

    internal static bool TryParse(string? text, [NotNullWhen(true)] out UpdatePublicKey? key, [NotNullWhen(false)] out string? error)
    {
        key = null;
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

        key = new UpdatePublicKey(bytes, Convert.ToBase64String(bytes));
        return true;
    }

    internal static UpdatePublicKey FromSubjectPublicKeyInfo(byte[] subjectPublicKeyInfo) =>
        new(subjectPublicKeyInfo, Convert.ToBase64String(subjectPublicKeyInfo));

    /// <summary>Checks a DER ECDSA-SHA256 signature over <paramref name="data"/>.</summary>
    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> derSignature)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(_subjectPublicKeyInfo, out _);
        try
        {
            return ecdsa.VerifyData(data, derSignature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Checks that <paramref name="spki"/> is a SubjectPublicKeyInfo for id-ecPublicKey on the P-256 curve.</summary>
    internal static bool IsP256SubjectPublicKeyInfo(byte[] spki, [NotNullWhen(false)] out string? error)
    {
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

    public bool Equals(UpdatePublicKey? other) => other is not null && string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as UpdatePublicKey);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    public override string ToString() => Text;
}
