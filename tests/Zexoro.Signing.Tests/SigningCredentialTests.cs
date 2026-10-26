using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Zexoro.Signing.Fixtures;

namespace Zexoro.Signing.Tests;

public class SigningCredentialTests
{
    private const string Password = "correct horse";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadPfx_SingleCertificate_LoadsKey(bool ecdsa)
    {
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa);
        using var pfx = new TempFile(certificate.Export(X509ContentType.Pkcs12, Password));

        using var credential = SigningCredential.LoadPfx(pfx.Path, Password);

        Assert.Equal(certificate.RawData, credential.Certificate.RawData);
        Assert.True(credential.Certificate.HasPrivateKey);
        Assert.Empty(credential.Chain);
    }

    [Fact]
    public void LoadPfx_WithChain_SeparatesSignerFromChain()
    {
        var (root, intermediate, leaf) = TestCertificates.CreateChain();
        using (root)
        using (intermediate)
        using (leaf)
        using (var rootWithoutKey = X509CertificateLoader.LoadCertificate(root.RawData))
        {
            X509Certificate2Collection contents = [intermediate, leaf, rootWithoutKey];
            using var pfx = new TempFile(contents.Export(X509ContentType.Pkcs12, Password)!);

            using var credential = SigningCredential.LoadPfx(pfx.Path, Password);

            Assert.Equal(leaf.RawData, credential.Certificate.RawData);
            Assert.Equal(2, credential.Chain.Count);
            Assert.Contains(credential.Chain, c => c.RawData.AsSpan().SequenceEqual(intermediate.RawData));
            Assert.Contains(credential.Chain, c => c.RawData.AsSpan().SequenceEqual(root.RawData));
        }
    }

    [Fact]
    public void LoadPfx_WrongPassword_ThrowsSigningException()
    {
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa: false);
        using var pfx = new TempFile(certificate.Export(X509ContentType.Pkcs12, Password));

        var ex = Assert.Throws<SigningException>(() => SigningCredential.LoadPfx(pfx.Path, "wrong"));
        Assert.Contains(pfx.Path, ex.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<CryptographicException>(ex.InnerException);
    }

    [Fact]
    public void LoadPfx_MissingFile_ThrowsSigningException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zexoro-missing-{Guid.NewGuid():N}.pfx");

        Assert.Throws<SigningException>(() => SigningCredential.LoadPfx(path, Password));
    }

    [Fact]
    public void LoadPfx_NoPrivateKey_ThrowsSigningException()
    {
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa: true);
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
        X509Certificate2Collection contents = [publicOnly];
        using var pfx = new TempFile(contents.Export(X509ContentType.Pkcs12, Password)!);

        var ex = Assert.Throws<SigningException>(() => SigningCredential.LoadPfx(pfx.Path, Password));
        Assert.Contains("no private key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_CertificateWithoutKey_Throws()
    {
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa: true);
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);

        Assert.Throws<SigningException>(() => new SigningCredential(publicOnly));
    }
}
