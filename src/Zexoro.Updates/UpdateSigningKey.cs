using System.Security.Cryptography;

namespace Zexoro.Updates;

/// <summary>
/// The developer's private feed key (ECDSA P-256), stored as PKCS #8 PEM, optionally encrypted
/// (AES-256-CBC with a PBKDF2-SHA256 derived key). It signs feeds; it never ships in an install.
/// </summary>
public sealed class UpdateSigningKey : IDisposable
{
    private const int Pbkdf2Iterations = 600_000;

    private readonly ECDsa _key;

    private UpdateSigningKey(ECDsa key)
    {
        _key = key;
        PublicKey = UpdatePublicKey.FromSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The matching public key, to list in <c>[updates] keys</c>.</summary>
    public UpdatePublicKey PublicKey { get; }

    /// <summary>Creates a new random key.</summary>
    public static UpdateSigningKey Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Reads a PKCS #8 PEM key (<c>PRIVATE KEY</c> or <c>ENCRYPTED PRIVATE KEY</c>).</summary>
    /// <param name="pem">The PEM text.</param>
    /// <param name="password">The password of an encrypted key; ignored for an unencrypted one.</param>
    /// <exception cref="UpdateException">
    /// The text is not such a key, the key is encrypted and the password is missing or wrong, or the
    /// key is not on the P-256 curve (<see cref="UpdateErrorKind.InvalidKey"/>).
    /// </exception>
    public static UpdateSigningKey FromPem(string pem, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(pem);
        var encrypted = pem.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal);
        if (!encrypted && !pem.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal))
        {
            throw new UpdateException(UpdateErrorKind.InvalidKey, "not a PKCS #8 private key (expected '-----BEGIN PRIVATE KEY-----' or '-----BEGIN ENCRYPTED PRIVATE KEY-----')");
        }

        if (encrypted && string.IsNullOrEmpty(password))
        {
            throw new UpdateException(UpdateErrorKind.InvalidKey, "the private key is encrypted and no password was given");
        }

        var key = ECDsa.Create();
        try
        {
            if (encrypted)
            {
                key.ImportFromEncryptedPem(pem, password);
            }
            else
            {
                key.ImportFromPem(pem);
            }

            if (!UpdatePublicKey.IsP256SubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo(), out var error))
            {
                throw new UpdateException(UpdateErrorKind.InvalidKey, $"the private key is not usable: {error}");
            }

            return new UpdateSigningKey(key);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            key.Dispose();
            throw new UpdateException(
                UpdateErrorKind.InvalidKey,
                encrypted ? "cannot read the private key: wrong password or damaged key" : $"cannot read the private key: {ex.Message}",
                ex);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>Returns the key as PKCS #8 PEM, encrypted when <paramref name="password"/> is not empty.</summary>
    public string ToPem(string? password = null) =>
        string.IsNullOrEmpty(password)
            ? _key.ExportPkcs8PrivateKeyPem()
            : _key.ExportEncryptedPkcs8PrivateKeyPem(
                password,
                new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, Pbkdf2Iterations));

    /// <summary>Returns a DER ECDSA-SHA256 signature over <paramref name="data"/>.</summary>
    public byte[] Sign(ReadOnlySpan<byte> data) =>
        _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    public void Dispose() => _key.Dispose();
}
