using System.Security.Cryptography;

namespace Zexoro.Updates.Tests;

public sealed class KeyTests
{
    [Fact]
    public void Pem_RoundTripsUnencrypted()
    {
        using var key = UpdateSigningKey.Create();
        var pem = key.ToPem();
        Assert.Contains("-----BEGIN PRIVATE KEY-----", pem, StringComparison.Ordinal);
        using var loaded = UpdateSigningKey.FromPem(pem);
        Assert.Equal(key.PublicKey, loaded.PublicKey);
    }

    [Fact]
    public void Pem_RoundTripsEncrypted()
    {
        using var key = UpdateSigningKey.Create();
        var pem = key.ToPem("correct horse");
        Assert.Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", pem, StringComparison.Ordinal);
        using var loaded = UpdateSigningKey.FromPem(pem, "correct horse");
        Assert.Equal(key.PublicKey, loaded.PublicKey);
    }

    [Fact]
    public void FromPem_RejectsWrongPassword()
    {
        using var key = UpdateSigningKey.Create();
        var ex = Assert.Throws<UpdateException>(() => UpdateSigningKey.FromPem(key.ToPem("right"), "wrong"));
        Assert.Equal(UpdateErrorKind.InvalidKey, ex.Kind);
        Assert.Contains("wrong password", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromPem_RejectsMissingPassword()
    {
        using var key = UpdateSigningKey.Create();
        var ex = Assert.Throws<UpdateException>(() => UpdateSigningKey.FromPem(key.ToPem("right")));
        Assert.Contains("encrypted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromPem_RejectsOtherCurves()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var ex = Assert.Throws<UpdateException>(() => UpdateSigningKey.FromPem(p384.ExportPkcs8PrivateKeyPem()));
        Assert.Contains("P-256", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FromPem_RejectsRsaAndGarbage()
    {
        using var rsa = RSA.Create(2048);
        Assert.Throws<UpdateException>(() => UpdateSigningKey.FromPem(rsa.ExportPkcs8PrivateKeyPem()));
        Assert.Throws<UpdateException>(() => UpdateSigningKey.FromPem("hello"));
        Assert.Throws<UpdateException>(() => UpdateSigningKey.FromPem("-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----\n"));
    }

    [Fact]
    public void PublicKey_IsBase64SubjectPublicKeyInfo()
    {
        using var key = UpdateSigningKey.Create();
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKey.Text), out _);
        Assert.Equal(256, ecdsa.KeySize);
        Assert.StartsWith("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE", key.PublicKey.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicKey_ParseAcceptsWhitespaceAndRoundTrips()
    {
        using var key = UpdateSigningKey.Create();
        var parsed = UpdatePublicKey.Parse("  " + key.PublicKey.Text + "\n");
        Assert.Equal(key.PublicKey, parsed);
        Assert.Equal(key.PublicKey.Text, parsed.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void PublicKey_ParseRejectsInvalidText(string text)
    {
        Assert.False(UpdatePublicKey.TryParse(text, out _));
        Assert.Equal(UpdateErrorKind.InvalidKey, Assert.Throws<UpdateException>(() => UpdatePublicKey.Parse(text)).Kind);
    }

    [Fact]
    public void PublicKey_ParseRejectsOtherCurvesAndRsa()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.False(UpdatePublicKey.TryParse(Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo()), out _));
        using var rsa = RSA.Create(2048);
        Assert.False(UpdatePublicKey.TryParse(Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()), out _));
    }

    [Fact]
    public void PublicKey_ParseRejectsTrailingData()
    {
        using var key = UpdateSigningKey.Create();
        var bytes = Convert.FromBase64String(key.PublicKey.Text);
        Assert.False(UpdatePublicKey.TryParse(Convert.ToBase64String([.. bytes, 0]), out _));
    }
}
