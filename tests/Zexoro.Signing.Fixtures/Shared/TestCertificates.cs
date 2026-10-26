using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Zexoro.Signing.Fixtures;

/// <summary>Throwaway code signing certificates for tests.</summary>
public static class TestCertificates
{
    private const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";

    /// <summary>A self-signed code signing certificate with an RSA 2048 or ECDSA P-256 key.</summary>
    public static X509Certificate2 CreateSelfSigned(bool ecdsa, string commonName = "zexoro test signer")
    {
        var request = ecdsa
            ? new CertificateRequest($"CN={commonName}", ECDsa.Create(ECCurve.NamedCurves.nistP256), HashAlgorithmName.SHA256)
            : new CertificateRequest($"CN={commonName}", RSA.Create(2048), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddLeafExtensions(request);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    /// <summary>
    /// An RSA root CA, an RSA intermediate CA and an ECDSA code signing leaf issued by the intermediate.
    /// Only the leaf has a private key.
    /// </summary>
    public static (X509Certificate2 Root, X509Certificate2 Intermediate, X509Certificate2 Leaf) CreateChain()
    {
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(30);

        using var rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=zexoro test root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddCaExtensions(rootRequest);
        var root = rootRequest.CreateSelfSigned(notBefore, notAfter.AddDays(1));

        using var intermediateKey = RSA.Create(2048);
        var intermediateRequest = new CertificateRequest("CN=zexoro test intermediate", intermediateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddCaExtensions(intermediateRequest);
        intermediateRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));
        var intermediate = intermediateRequest.Create(root, notBefore, notAfter, [1, 2]);

        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafRequest = new CertificateRequest("CN=zexoro test chained signer", leafKey, HashAlgorithmName.SHA256);
        AddLeafExtensions(leafRequest);
        leafRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(intermediate, true, false));
        using var leafPublic = leafRequest.Create(
            intermediate.SubjectName,
            X509SignatureGenerator.CreateForRSA(intermediateKey, RSASignaturePadding.Pkcs1),
            notBefore,
            notAfter.AddDays(-1),
            [3, 4, 5]);
        var leaf = leafPublic.CopyWithPrivateKey(leafKey);

        return (root, intermediate, leaf);
    }

    private static void AddLeafExtensions(CertificateRequest request)
    {
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(CodeSigningEku)], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
    }

    private static void AddCaExtensions(CertificateRequest request)
    {
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
    }
}
