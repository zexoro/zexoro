using System.Security.Cryptography;

namespace Zexoro.Signing;

/// <summary>
/// An RSA private key that signs apk packages as <c>abuild-sign -t RSA256</c> does
/// (docs/design/0006-desktop-formats.md, "As implemented (step 8)"): an RSA PKCS #1 v1.5 signature
/// with SHA-256 over the package's compressed control segment.
/// </summary>
/// <remarks>
/// The key is PEM text as <c>abuild-keygen</c> and <c>openssl genrsa</c> write it: PKCS #8
/// (<c>BEGIN PRIVATE KEY</c>) or PKCS #1 (<c>BEGIN RSA PRIVATE KEY</c>), unencrypted. RSA PKCS #1
/// v1.5 signatures are deterministic, so a signed package has the same bytes wherever it is packed.
/// </remarks>
public sealed class ApkSigningKey : IDisposable
{
    /// <summary>The smallest key size accepted; abuild-keygen creates 4096-bit keys by default.</summary>
    public const int MinimumBits = 2048;

    private readonly RSA _rsa;

    private ApkSigningKey(RSA rsa)
    {
        _rsa = rsa;
    }

    /// <summary>The key size in bits.</summary>
    public int Bits => _rsa.KeySize;

    /// <summary>A short description for the log: <c>RSA 4096-bit</c>.</summary>
    public string Description => $"RSA {Bits}-bit";

    /// <summary>Loads an RSA private key from PEM text.</summary>
    /// <exception cref="SigningException">The text holds no usable RSA private key.</exception>
    public static ApkSigningKey Load(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        if (pem.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal)
            || pem.Contains("Proc-Type: 4,ENCRYPTED", StringComparison.Ordinal))
        {
            throw new SigningException("the apk signing key is encrypted; apk keys are used unencrypted, as abuild-keygen writes them (protect the key through the environment variable or file that holds it)");
        }

        if (pem.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) && !pem.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
        {
            throw new SigningException("the apk signing key is a public key; [linux.apk.signing] key needs the private key (the .rsa file abuild-keygen writes, not the .rsa.pub)");
        }

        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            rsa.Dispose();
            throw new SigningException(
                $"the apk signing key is not a PEM RSA private key ('-----BEGIN PRIVATE KEY-----' or '-----BEGIN RSA PRIVATE KEY-----'): {ex.Message}", ex);
        }

        try
        {
            // ImportFromPem also accepts a public key; signing needs the private part.
            rsa.ExportParameters(includePrivateParameters: true);
        }
        catch (CryptographicException ex)
        {
            rsa.Dispose();
            throw new SigningException("the apk signing key holds no RSA private key; [linux.apk.signing] key needs the private key (the .rsa file abuild-keygen writes)", ex);
        }

        if (rsa.KeySize < MinimumBits)
        {
            var bits = rsa.KeySize;
            rsa.Dispose();
            throw new SigningException($"the apk signing key has {bits} bits; at least {MinimumBits} are required (abuild-keygen creates 4096-bit keys)");
        }

        return new ApkSigningKey(rsa);
    }

    /// <summary>Signs <paramref name="data"/> (the compressed control segment): RSA PKCS #1 v1.5 over its SHA-256.</summary>
    public byte[] Sign(ReadOnlySpan<byte> data) => _rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    /// <summary>
    /// The public key as PEM (<c>BEGIN PUBLIC KEY</c>, a SubjectPublicKeyInfo), the form
    /// <c>/etc/apk/keys</c> holds.
    /// </summary>
    public string ExportPublicKeyPem() => _rsa.ExportSubjectPublicKeyInfoPem();

    public void Dispose() => _rsa.Dispose();
}
