using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Zexoro.Signing.Fixtures;

namespace Zexoro.Signing.Tests;

public class SignedDataTests
{
    private const string Sha256 = "2.16.840.1.101.3.4.2.1";
    private const string IndirectData = "1.3.6.1.4.1.311.2.1.4";
    private static readonly Asn1Tag Context0 = new(TagClass.ContextSpecific, 0, true);
    private static readonly Asn1Tag Context1 = new(TagClass.ContextSpecific, 1, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SignFile_SelfSigned_WritesVerifiableSignedData(bool ecdsa)
    {
        var bytes = TestPe.Build(pe32Plus: true, overlayLength: 13);
        var imageDigest = AuthenticodeSigner.ComputeDigest(new MemoryStream(bytes));
        using var file = new TempFile(bytes);
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa);
        using var credential = new SigningCredential(certificate);

        AuthenticodeSigner.SignFile(file.Path, new SigningOptions
        {
            Credential = credential,
            Description = "Example Notes",
            DescriptionUrl = new Uri("https://example.com/notes"),
        });

        var signature = Parse(SignedImage.SignedData(file.ReadAllBytes(), pe32Plus: true));
        Assert.Equal(imageDigest, signature.ImageDigest);
        Assert.Equal(Sha256, signature.ImageDigestAlgorithm);
        Assert.Equal([certificate.RawData], signature.Certificates);
        Assert.Equal(certificate.IssuerName.RawData, signature.Issuer);
        Assert.Equal(certificate.SerialNumberBytes.ToArray(), signature.SerialNumber);
        Assert.Equal(ecdsa ? "1.2.840.10045.4.3.2" : "1.2.840.113549.1.1.1", signature.SignatureAlgorithm);
        Assert.Equal(IndirectData, signature.Attributes["1.2.840.113549.1.9.3"].ReadObjectIdentifier());
        Assert.Equal(SHA256.HashData(signature.IndirectDataContents), signature.Attributes["1.2.840.113549.1.9.4"].ReadOctetString());
        Assert.Equal("1.3.6.1.4.1.311.2.1.21", signature.Attributes["1.3.6.1.4.1.311.2.1.11"].ReadSequence().ReadObjectIdentifier());

        var opus = signature.Attributes["1.3.6.1.4.1.311.2.1.12"].ReadSequence();
        Assert.Equal("Example Notes", opus.ReadSequence(Context0)
            .ReadCharacterString(UniversalTagNumber.BMPString, new Asn1Tag(TagClass.ContextSpecific, 0)));
        Assert.Equal("https://example.com/notes", opus.ReadSequence(Context1)
            .ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 0)));

        Assert.True(VerifySignature(certificate, signature.SignedAttributes, signature.SignatureValue));
        Assert.Null(signature.UnsignedAttributes);
    }

    [Fact]
    public void SignFile_Chain_EmbedsChainCertificates()
    {
        var (root, intermediate, leaf) = TestCertificates.CreateChain();
        using (root)
        using (intermediate)
        using (leaf)
        using (var credential = new SigningCredential(leaf, [intermediate, leaf]))
        using (var file = new TempFile(TestPe.Build(pe32Plus: false)))
        {
            AuthenticodeSigner.SignFile(file.Path, new SigningOptions { Credential = credential });

            var signature = Parse(SignedImage.SignedData(file.ReadAllBytes(), pe32Plus: false));
            Assert.Equal(2, signature.Certificates.Count);
            Assert.Contains(leaf.RawData, signature.Certificates);
            Assert.Contains(intermediate.RawData, signature.Certificates);
            Assert.Equal(intermediate.SubjectName.RawData, signature.Issuer);
            Assert.True(VerifySignature(leaf, signature.SignedAttributes, signature.SignatureValue));
        }
    }

    [Fact]
    public void BuildIndirectDataContent_MatchesSigntoolLayout()
    {
        var digest = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        var der = AuthenticodeSignedData.BuildIndirectDataContent(digest);

        // SpcPeImageData with empty flags and the file link "<<<Obsolete>>>" (BMPString), then DigestInfo.
        var expected = Convert.FromHexString(
            "3068" + "3033" + "060A2B0601040182370201" + "0F" +
            "3025" + "030100" + "A020" + "A21E" + "801C" +
            "003C003C003C004F00620073006F006C006500740065003E003E003E" +
            "3031" + "300D" + "0609608648016503040201" + "0500" + "0420" + Convert.ToHexString(digest));
        Assert.Equal(expected, der);
    }

    [Fact]
    public void BuildSignedAttributes_NoDescription_WritesEmptyOpusInfo()
    {
        var der = AuthenticodeSignedData.BuildSignedAttributes(new byte[32], description: null, descriptionUrl: null);

        var attributes = ReadAttributes(new AsnReader(der, AsnEncodingRules.DER).ReadSetOf());
        Assert.Equal(4, attributes.Count);
        Assert.False(attributes["1.3.6.1.4.1.311.2.1.12"].ReadSequence().HasData);
    }

    [Fact]
    public void SignFile_WithTimestamp_EmbedsTokenAsUnsignedAttribute()
    {
        using var authority = new FakeTimestampAuthority();
        using var client = new HttpClient(authority);
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa: false);
        using var credential = new SigningCredential(certificate);
        using var file = new TempFile(TestPe.Build(pe32Plus: true));

        AuthenticodeSigner.SignFile(file.Path, new SigningOptions
        {
            Credential = credential,
            TimestampUrl = new Uri("http://tsa.invalid/"),
            HttpClient = client,
        });

        var signature = Parse(SignedImage.SignedData(file.ReadAllBytes(), pe32Plus: true));
        Assert.Equal("application/timestamp-query", authority.LastContentType);
        Assert.Equal(SHA256.HashData(signature.SignatureValue), authority.LastImprint);
        Assert.NotNull(signature.UnsignedAttributes);
        var unsigned = ReadAttributes(signature.UnsignedAttributes);
        Assert.Equal(authority.LastToken, unsigned["1.3.6.1.4.1.311.3.3.1"].ReadEncodedValue().ToArray());
    }

    internal static ParsedSignature Parse(byte[] der)
    {
        var contentInfo = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
        Assert.Equal("1.2.840.113549.1.7.2", contentInfo.ReadObjectIdentifier());
        var signedData = contentInfo.ReadSequence(Context0).ReadSequence();
        Assert.False(contentInfo.HasData);

        Assert.Equal(1, (int)signedData.ReadInteger());
        var digestAlgorithms = signedData.ReadSetOf();
        Assert.Equal(Sha256, digestAlgorithms.ReadSequence().ReadObjectIdentifier());
        Assert.False(digestAlgorithms.HasData);

        var content = signedData.ReadSequence();
        Assert.Equal(IndirectData, content.ReadObjectIdentifier());
        var indirectDataDer = content.ReadSequence(Context0).ReadEncodedValue().ToArray();
        AsnDecoder.ReadSequence(indirectDataDer, AsnEncodingRules.DER, out var contentOffset, out var contentLength, out _);
        var indirect = new AsnReader(indirectDataDer, AsnEncodingRules.DER).ReadSequence();
        var data = indirect.ReadSequence();
        Assert.Equal("1.3.6.1.4.1.311.2.1.15", data.ReadObjectIdentifier());
        var digestInfo = indirect.ReadSequence();
        var digestAlgorithm = digestInfo.ReadSequence().ReadObjectIdentifier();
        var imageDigest = digestInfo.ReadOctetString();

        var certificates = new List<byte[]>();
        var certificateSet = signedData.ReadSetOf(Context0);
        while (certificateSet.HasData)
        {
            certificates.Add(certificateSet.ReadEncodedValue().ToArray());
        }

        var signerInfos = signedData.ReadSetOf();
        Assert.False(signedData.HasData);
        var signerInfo = signerInfos.ReadSequence();
        Assert.False(signerInfos.HasData);

        Assert.Equal(1, (int)signerInfo.ReadInteger());
        var sid = signerInfo.ReadSequence();
        var issuer = sid.ReadEncodedValue().ToArray();
        var serial = sid.ReadIntegerBytes().ToArray();
        Assert.Equal(Sha256, signerInfo.ReadSequence().ReadObjectIdentifier());

        var implicitAttributes = signerInfo.ReadEncodedValue().ToArray();
        Assert.Equal(0xA0, implicitAttributes[0]);
        var signedAttributes = (byte[])implicitAttributes.Clone();
        signedAttributes[0] = 0x31;
        var attributes = ReadAttributes(new AsnReader(signedAttributes, AsnEncodingRules.DER).ReadSetOf());

        var signatureAlgorithm = signerInfo.ReadSequence().ReadObjectIdentifier();
        var signatureValue = signerInfo.ReadOctetString();
        AsnReader? unsignedAttributes = signerInfo.HasData ? signerInfo.ReadSetOf(Context1) : null;
        Assert.False(signerInfo.HasData);

        return new ParsedSignature(
            indirectDataDer.AsSpan(contentOffset, contentLength).ToArray(),
            digestAlgorithm,
            imageDigest,
            certificates,
            issuer,
            serial,
            signedAttributes,
            attributes,
            signatureAlgorithm,
            signatureValue,
            unsignedAttributes);
    }

    /// <summary>Attribute type to a reader over its single value.</summary>
    private static Dictionary<string, AsnReader> ReadAttributes(AsnReader set)
    {
        var result = new Dictionary<string, AsnReader>();
        while (set.HasData)
        {
            var attribute = set.ReadSequence();
            var type = attribute.ReadObjectIdentifier();
            var values = attribute.ReadSetOf();
            var value = new AsnReader(values.ReadEncodedValue(), AsnEncodingRules.DER);
            Assert.False(values.HasData);
            result.Add(type, value);
        }

        return result;
    }

    private static bool VerifySignature(X509Certificate2 certificate, byte[] data, byte[] signature)
    {
        using var rsa = certificate.GetRSAPublicKey();
        if (rsa is not null)
        {
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        using var ecdsa = certificate.GetECDsaPublicKey()!;
        return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    internal sealed record ParsedSignature(
        byte[] IndirectDataContents,
        string ImageDigestAlgorithm,
        byte[] ImageDigest,
        List<byte[]> Certificates,
        byte[] Issuer,
        byte[] SerialNumber,
        byte[] SignedAttributes,
        Dictionary<string, AsnReader> Attributes,
        string SignatureAlgorithm,
        byte[] SignatureValue,
        AsnReader? UnsignedAttributes);
}
