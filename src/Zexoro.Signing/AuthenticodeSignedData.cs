using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Zexoro.Signing;

/// <summary>Builds the PKCS #7 / CMS <c>SignedData</c> that carries an Authenticode signature.</summary>
/// <remarks>
/// <code>
/// ContentInfo ::= SEQUENCE { contentType signedData, [0] EXPLICIT SignedData }
/// SignedData ::= SEQUENCE {
///   version 1, digestAlgorithms SET { sha256 },
///   contentInfo SEQUENCE { SPC_INDIRECT_DATA_OBJID, [0] EXPLICIT SpcIndirectDataContent },
///   certificates [0] IMPLICIT SET OF Certificate,
///   signerInfos SET { SignerInfo } }
/// SpcIndirectDataContent ::= SEQUENCE {
///   data SEQUENCE { SPC_PE_IMAGE_DATAOBJ, SpcPeImageData },
///   messageDigest DigestInfo }
/// </code>
/// The signer's messageDigest attribute hashes the DER contents of SpcIndirectDataContent
/// without its outer tag and length, as Authenticode requires.
/// </remarks>
internal static class AuthenticodeSignedData
{
    private static readonly Asn1Tag Context0 = new(TagClass.ContextSpecific, 0, isConstructed: true);
    private static readonly Asn1Tag Context1 = new(TagClass.ContextSpecific, 1, isConstructed: true);
    private static readonly Asn1Tag Context2 = new(TagClass.ContextSpecific, 2, isConstructed: true);
    private static readonly Asn1Tag Context0Primitive = new(TagClass.ContextSpecific, 0);

    /// <summary>Builds the signature for an image digest.</summary>
    /// <param name="imageDigest">The SHA-256 Authenticode digest of the PE image.</param>
    /// <param name="credential">Signing certificate, key and chain.</param>
    /// <param name="description">SpcSpOpusInfo program name, optional.</param>
    /// <param name="descriptionUrl">SpcSpOpusInfo URL, optional.</param>
    /// <param name="timestamp">
    /// Called with the signature value; returns an RFC 3161 timestamp token (a DER ContentInfo)
    /// to embed, or <see langword="null"/> to sign without a timestamp.
    /// </param>
    public static byte[] Build(
        ReadOnlySpan<byte> imageDigest,
        SigningCredential credential,
        string? description,
        Uri? descriptionUrl,
        Func<byte[], byte[]?>? timestamp)
    {
        var indirectData = BuildIndirectDataContent(imageDigest);
        var indirectDataContents = SequenceContents(indirectData);
        var contentDigest = SHA256.HashData(indirectDataContents);

        var signedAttributes = BuildSignedAttributes(contentDigest, description, descriptionUrl);
        var signature = Sign(credential.Certificate, signedAttributes);
        var token = timestamp?.Invoke(signature);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(Oids.SignedData);
            using (writer.PushSequence(Context0))
            using (writer.PushSequence())
            {
                writer.WriteInteger(1);
                using (writer.PushSetOf())
                {
                    WriteAlgorithmIdentifier(writer, Oids.Sha256, withNullParameters: true);
                }

                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(Oids.SpcIndirectDataContent);
                    using (writer.PushSequence(Context0))
                    {
                        writer.WriteEncodedValue(indirectData);
                    }
                }

                using (writer.PushSetOf(Context0))
                {
                    writer.WriteEncodedValue(credential.Certificate.RawData);
                    foreach (var certificate in credential.Chain)
                    {
                        writer.WriteEncodedValue(certificate.RawData);
                    }
                }

                using (writer.PushSetOf())
                {
                    WriteSignerInfo(writer, credential.Certificate, signedAttributes, signature, token);
                }
            }
        }

        return writer.Encode();
    }

    internal static byte[] BuildIndirectDataContent(ReadOnlySpan<byte> imageDigest)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(Oids.SpcPeImageData);

                // SpcPeImageData ::= SEQUENCE { flags BIT STRING, file [0] EXPLICIT SpcLink }
                // with the flags empty and the link a file name "<<<Obsolete>>>", as signtool writes it.
                using (writer.PushSequence())
                {
                    writer.WriteBitString([]);
                    using (writer.PushSequence(Context0))
                    using (writer.PushSequence(Context2))
                    {
                        writer.WriteCharacterString(UniversalTagNumber.BMPString, "<<<Obsolete>>>", Context0Primitive);
                    }
                }
            }

            using (writer.PushSequence())
            {
                WriteAlgorithmIdentifier(writer, Oids.Sha256, withNullParameters: true);
                writer.WriteOctetString(imageDigest);
            }
        }

        return writer.Encode();
    }

    /// <summary>The DER encoding of the authenticated attributes as a universal SET OF (what is signed).</summary>
    internal static byte[] BuildSignedAttributes(ReadOnlySpan<byte> contentDigest, string? description, Uri? descriptionUrl)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSetOf())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(Oids.ContentType);
                using (writer.PushSetOf())
                {
                    writer.WriteObjectIdentifier(Oids.SpcIndirectDataContent);
                }
            }

            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(Oids.MessageDigest);
                using (writer.PushSetOf())
                {
                    writer.WriteOctetString(contentDigest);
                }
            }

            // SpcSpOpusInfo ::= SEQUENCE { programName [0] EXPLICIT SpcString OPTIONAL,
            //                              moreInfo [1] EXPLICIT SpcLink OPTIONAL }
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(Oids.SpcSpOpusInfo);
                using (writer.PushSetOf())
                using (writer.PushSequence())
                {
                    if (!string.IsNullOrEmpty(description))
                    {
                        using (writer.PushSequence(Context0))
                        {
                            writer.WriteCharacterString(UniversalTagNumber.BMPString, description, Context0Primitive);
                        }
                    }

                    if (descriptionUrl is not null)
                    {
                        using (writer.PushSequence(Context1))
                        {
                            writer.WriteCharacterString(UniversalTagNumber.IA5String, descriptionUrl.AbsoluteUri, Context0Primitive);
                        }
                    }
                }
            }

            // SpcStatementType ::= SEQUENCE OF OBJECT IDENTIFIER
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(Oids.SpcStatementType);
                using (writer.PushSetOf())
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(Oids.SpcIndividualCodeSigning);
                }
            }
        }

        return writer.Encode();
    }

    private static void WriteSignerInfo(
        AsnWriter writer,
        X509Certificate2 certificate,
        byte[] signedAttributes,
        byte[] signature,
        byte[]? timestampToken)
    {
        using (writer.PushSequence())
        {
            writer.WriteInteger(1);

            // IssuerAndSerialNumber
            using (writer.PushSequence())
            {
                writer.WriteEncodedValue(certificate.IssuerName.RawData);
                writer.WriteInteger(certificate.SerialNumberBytes.Span);
            }

            WriteAlgorithmIdentifier(writer, Oids.Sha256, withNullParameters: true);

            // authenticatedAttributes [0] IMPLICIT: the signed SET OF with its tag replaced.
            var implicitAttributes = (byte[])signedAttributes.Clone();
            implicitAttributes[0] = 0xA0;
            writer.WriteEncodedValue(implicitAttributes);

            if (IsRsa(certificate))
            {
                WriteAlgorithmIdentifier(writer, Oids.RsaEncryption, withNullParameters: true);
            }
            else
            {
                WriteAlgorithmIdentifier(writer, Oids.EcdsaWithSha256, withNullParameters: false);
            }

            writer.WriteOctetString(signature);

            if (timestampToken is not null)
            {
                using (writer.PushSetOf(Context1))
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(Oids.Rfc3161CounterSignature);
                    using (writer.PushSetOf())
                    {
                        try
                        {
                            writer.WriteEncodedValue(timestampToken);
                        }
                        catch (ArgumentException ex)
                        {
                            throw new SigningException("The timestamp token is not DER encoded.", ex);
                        }
                    }
                }
            }
        }
    }

    private static byte[] Sign(X509Certificate2 certificate, byte[] data)
    {
        if (IsRsa(certificate))
        {
            using var rsa = certificate.GetRSAPrivateKey()
                ?? throw new SigningException($"The RSA private key of '{certificate.Subject}' is not accessible.");
            return rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        using var ecdsa = certificate.GetECDsaPrivateKey()
            ?? throw new SigningException($"The ECDSA private key of '{certificate.Subject}' is not accessible.");
        return ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    private static bool IsRsa(X509Certificate2 certificate) => certificate.GetKeyAlgorithm() == Oids.RsaEncryption;

    private static void WriteAlgorithmIdentifier(AsnWriter writer, string oid, bool withNullParameters)
    {
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(oid);
            if (withNullParameters)
            {
                writer.WriteNull();
            }
        }
    }

    private static ReadOnlySpan<byte> SequenceContents(byte[] der)
    {
        AsnDecoder.ReadSequence(der, AsnEncodingRules.DER, out var offset, out var length, out _);
        return der.AsSpan(offset, length);
    }
}
