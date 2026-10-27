using System.Security.Cryptography;
using System.Text;

namespace Zexoro.Updates.Tests;

public sealed class SignatureTests
{
    private static readonly byte[] Feed = TestFeeds.Utf8(TestFeeds.SampleJson);

    [Fact]
    public void Sign_ProducesOneLineOfBase64Der()
    {
        using var key = UpdateSigningKey.Create();
        var file = FeedSignature.Sign(Feed, key);
        var text = Encoding.ASCII.GetString(file);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        // DER, verifiable by any ECDSA implementation (and openssl dgst -verify).
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.PublicKey.Text), out _);
        Assert.True(ecdsa.VerifyData(Feed, Convert.FromBase64String(text), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    [Fact]
    public void Verify_AcceptsTheSigningKey()
    {
        using var key = UpdateSigningKey.Create();
        Assert.True(FeedSignature.Verify(Feed, FeedSignature.Sign(Feed, key), [key.PublicKey]));
    }

    [Fact]
    public void Verify_RejectsAnyChangedByte()
    {
        using var key = UpdateSigningKey.Create();
        var signature = FeedSignature.Sign(Feed, key);
        for (var i = 0; i < Feed.Length; i += 37)
        {
            var tampered = (byte[])Feed.Clone();
            tampered[i] ^= 0x01;
            Assert.False(FeedSignature.Verify(tampered, signature, [key.PublicKey]), $"byte {i}");
        }

        Assert.False(FeedSignature.Verify([.. Feed, (byte)' '], signature, [key.PublicKey]));
        Assert.False(FeedSignature.Verify(Feed.AsSpan(0, Feed.Length - 1), signature, [key.PublicKey]));
    }

    [Fact]
    public void Verify_RejectsAnotherKey()
    {
        using var key = UpdateSigningKey.Create();
        using var other = UpdateSigningKey.Create();
        Assert.False(FeedSignature.Verify(Feed, FeedSignature.Sign(Feed, other), [key.PublicKey]));
    }

    [Fact]
    public void Verify_AcceptsAnyKeyOfTheTrustedList()
    {
        using var oldKey = UpdateSigningKey.Create();
        using var newKey = UpdateSigningKey.Create();
        UpdatePublicKey[] trusted = [oldKey.PublicKey, newKey.PublicKey];
        Assert.Equal(oldKey.PublicKey, FeedSignature.FindSigner(Feed, FeedSignature.Sign(Feed, oldKey), trusted));
        Assert.Equal(newKey.PublicKey, FeedSignature.FindSigner(Feed, FeedSignature.Sign(Feed, newKey), trusted));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("not base64\n")]
    [InlineData("AAAA\n")]
    public void Verify_RejectsMalformedSignatureFiles(string signature)
    {
        using var key = UpdateSigningKey.Create();
        Assert.False(FeedSignature.Verify(Feed, Encoding.ASCII.GetBytes(signature), [key.PublicKey]));
    }

    [Fact]
    public void Verify_RejectsTruncatedAndOversizedSignatures()
    {
        using var key = UpdateSigningKey.Create();
        var der = Convert.FromBase64String(Encoding.ASCII.GetString(FeedSignature.Sign(Feed, key)));
        var truncated = Encoding.ASCII.GetBytes(Convert.ToBase64String(der.AsSpan(0, der.Length - 2)));
        Assert.False(FeedSignature.Verify(Feed, truncated, [key.PublicKey]));
        Assert.False(FeedSignature.Verify(Feed, new byte[FeedSignature.MaxFileSize + 1], [key.PublicKey]));
    }

    [Fact]
    public void Verify_AcceptsCrlfLineEnd()
    {
        using var key = UpdateSigningKey.Create();
        var text = Encoding.ASCII.GetString(FeedSignature.Sign(Feed, key)).TrimEnd() + "\r\n";
        Assert.True(FeedSignature.Verify(Feed, Encoding.ASCII.GetBytes(text), [key.PublicKey]));
    }
}
