using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Zexoro.Updates;

/// <summary>
/// A trusted feed key: an ECDSA P-256 public key, written as base64 of its DER SubjectPublicKeyInfo
/// (docs/design/0002-update-contract.md section 5), the same text that <c>zexoro keys new</c> prints
/// and that install records list.
/// </summary>
public sealed class UpdatePublicKey : IEquatable<UpdatePublicKey>
{
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
        if (!UpdateRules.TryDecodePublicKey(text, out var bytes, out error))
        {
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
    internal static bool IsP256SubjectPublicKeyInfo(byte[] spki, [NotNullWhen(false)] out string? error) =>
        UpdateRules.IsP256SubjectPublicKeyInfo(spki, out error);

    public bool Equals(UpdatePublicKey? other) => other is not null && string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as UpdatePublicKey);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    public override string ToString() => Text;
}
