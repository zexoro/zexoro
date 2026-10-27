using System.Security.Cryptography;

namespace Zexoro.Signing.Tests;

public sealed class ApkSigningKeyTests
{
    [Fact]
    public void Load_Pkcs8Key_SignsWithRsaSha256Pkcs1()
    {
        using var rsa = RSA.Create(2048);
        using var key = ApkSigningKey.Load(rsa.ExportPkcs8PrivateKeyPem());
        byte[] data = [1, 2, 3, 4];

        var signature = key.Sign(data);

        Assert.Equal("RSA 2048-bit", key.Description);
        Assert.True(rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.Equal(signature, key.Sign(data));
        Assert.Equal(rsa.ExportSubjectPublicKeyInfoPem(), key.ExportPublicKeyPem());
    }

    [Fact]
    public void Load_Pkcs1Key_Works()
    {
        using var rsa = RSA.Create(3072);
        using var key = ApkSigningKey.Load(rsa.ExportRSAPrivateKeyPem());

        Assert.Equal(3072, key.Bits);
    }

    [Fact]
    public void Load_TheE2EKey_MatchesItsPublicKey()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "apk-signing");
        using var key = ApkSigningKey.Load(File.ReadAllText(Path.Combine(directory, "zexoro-e2e.rsa")));

        Assert.Equal(File.ReadAllText(Path.Combine(directory, "zexoro-e2e.rsa.pub")).ReplaceLineEndings("\n").Trim(), key.ExportPublicKeyPem().ReplaceLineEndings("\n").Trim());
    }

    [Fact]
    public void Load_PublicKey_IsRefused()
    {
        using var rsa = RSA.Create(2048);

        var ex = Assert.Throws<SigningException>(() => ApkSigningKey.Load(rsa.ExportSubjectPublicKeyInfoPem()));

        Assert.StartsWith("the apk signing key is a public key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ShortKey_IsRefused()
    {
        using var rsa = RSA.Create(1024);

        var ex = Assert.Throws<SigningException>(() => ApkSigningKey.Load(rsa.ExportPkcs8PrivateKeyPem()));

        Assert.Equal("the apk signing key has 1024 bits; at least 2048 are required (abuild-keygen creates 4096-bit keys)", ex.Message);
    }

    [Fact]
    public void Load_EncryptedKey_IsRefused()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportEncryptedPkcs8PrivateKeyPem("secret", new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000));

        var ex = Assert.Throws<SigningException>(() => ApkSigningKey.Load(pem));

        Assert.StartsWith("the apk signing key is encrypted", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a key")]
    [InlineData("-----BEGIN EC PRIVATE KEY-----\nMHcCAQEEIA==\n-----END EC PRIVATE KEY-----\n")]
    public void Load_OtherText_IsRefused(string text)
    {
        var ex = Assert.Throws<SigningException>(() => ApkSigningKey.Load(text));

        Assert.StartsWith("the apk signing key is not a PEM RSA private key", ex.Message, StringComparison.Ordinal);
    }
}
