using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Zexoro.Signing;

/// <summary>A code signing certificate with its private key, plus the chain certificates to embed.</summary>
public sealed class SigningCredential : IDisposable
{
    private readonly bool _ownsCertificates;

    /// <summary>Creates a credential from certificates the caller keeps ownership of.</summary>
    /// <param name="certificate">The signing certificate; it must have an RSA or ECDSA private key.</param>
    /// <param name="chain">Further certificates to embed in the signature (intermediates, optionally the root).</param>
    public SigningCredential(X509Certificate2 certificate, IEnumerable<X509Certificate2>? chain = null)
        : this(certificate, chain, ownsCertificates: false)
    {
    }

    private SigningCredential(X509Certificate2 certificate, IEnumerable<X509Certificate2>? chain, bool ownsCertificates)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey)
        {
            throw new SigningException($"The certificate '{certificate.Subject}' has no private key.");
        }

        var keyAlgorithm = certificate.GetKeyAlgorithm();
        if (keyAlgorithm != Oids.RsaEncryption && keyAlgorithm != Oids.EcPublicKey)
        {
            throw new SigningException(
                $"The certificate '{certificate.Subject}' uses key algorithm {keyAlgorithm}; only RSA and ECDSA keys are supported.");
        }

        Certificate = certificate;
        Chain = chain?.Where(c => !c.RawDataMemory.Span.SequenceEqual(certificate.RawDataMemory.Span)).ToArray() ?? [];
        _ownsCertificates = ownsCertificates;
    }

    /// <summary>The signing certificate (with private key).</summary>
    public X509Certificate2 Certificate { get; }

    /// <summary>Chain certificates embedded in the signature next to <see cref="Certificate"/>.</summary>
    public IReadOnlyList<X509Certificate2> Chain { get; }

    /// <summary>
    /// Loads a PKCS #12 (PFX) file. The certificate with a private key signs; every other
    /// certificate in the file is embedded as part of the chain.
    /// </summary>
    /// <exception cref="SigningException">The file cannot be read, the password is wrong, or the file does not hold exactly one private key.</exception>
    public static SigningCredential LoadPfx(string path, string? password)
    {
        ArgumentNullException.ThrowIfNull(path);

        X509Certificate2Collection collection;
        try
        {
            // macOS does not support ephemeral key sets; elsewhere they keep keys off the disk.
            var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
            collection = X509CertificateLoader.LoadPkcs12CollectionFromFile(path, password, flags);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new SigningException($"Cannot load the certificate file '{path}': {ex.Message}", ex);
        }

        var withKey = collection.Where(c => c.HasPrivateKey).ToList();
        if (withKey.Count != 1)
        {
            foreach (var c in collection)
            {
                c.Dispose();
            }

            throw new SigningException(withKey.Count == 0
                ? $"The certificate file '{path}' contains no private key."
                : $"The certificate file '{path}' contains {withKey.Count} private keys; expected exactly one.");
        }

        try
        {
            return new SigningCredential(withKey[0], collection.Where(c => c != withKey[0]), ownsCertificates: true);
        }
        catch
        {
            foreach (var c in collection)
            {
                c.Dispose();
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (!_ownsCertificates)
        {
            return;
        }

        Certificate.Dispose();
        foreach (var c in Chain)
        {
            c.Dispose();
        }
    }
}
