using System.Formats.Asn1;
using System.Net.Http.Headers;
using System.Numerics;
using System.Security.Cryptography;

namespace Zexoro.Signing;

/// <summary>Obtains RFC 3161 timestamp tokens over a signature value.</summary>
internal static class TimestampClient
{
    private const string RequestMediaType = "application/timestamp-query";

    /// <summary>Requests a token for <paramref name="signature"/> and returns its DER ContentInfo.</summary>
    /// <exception cref="SigningException">The authority cannot be reached or does not grant a matching token.</exception>
    public static byte[] GetToken(Uri url, byte[] signature, HttpClient? httpClient, TimeSpan timeout)
    {
        var imprint = SHA256.HashData(signature);
        var nonce = CreateNonce();
        var request = BuildRequest(imprint, nonce);

        byte[] response;
        var ownsClient = httpClient is null;
        var client = httpClient ?? new HttpClient { Timeout = timeout };
        try
        {
            using var content = new ByteArrayContent(request);
            content.Headers.ContentType = new MediaTypeHeaderValue(RequestMediaType);
            using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            using var reply = client.Send(message);
            if (!reply.IsSuccessStatusCode)
            {
                throw new SigningException(
                    $"The timestamp authority {url} answered HTTP {(int)reply.StatusCode} {reply.ReasonPhrase}.");
            }

            using var body = reply.Content.ReadAsStream();
            using var buffer = new MemoryStream();
            body.CopyTo(buffer);
            response = buffer.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            var reason = ex is TaskCanceledException ? "the request timed out" : ex.Message;
            throw new SigningException($"Cannot reach the timestamp authority {url}: {reason}", ex);
        }
        finally
        {
            if (ownsClient)
            {
                client.Dispose();
            }
        }

        return ParseResponse(response, imprint, nonce, url);
    }

    /// <summary>
    /// <code>
    /// TimeStampReq ::= SEQUENCE { version 1, messageImprint MessageImprint,
    ///                             nonce INTEGER, certReq TRUE }
    /// </code>
    /// </summary>
    internal static byte[] BuildRequest(ReadOnlySpan<byte> imprint, BigInteger nonce)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteInteger(1);
            WriteMessageImprint(writer, imprint);
            writer.WriteInteger(nonce);
            writer.WriteBoolean(true);
        }

        return writer.Encode();
    }

    /// <summary>
    /// Checks a <c>TimeStampResp</c> and returns the token in it. The token must be granted and
    /// carry the requested message imprint and nonce. The authority's own signature is not checked
    /// here; Windows checks it when it verifies the file.
    /// </summary>
    internal static byte[] ParseResponse(byte[] response, ReadOnlySpan<byte> imprint, BigInteger nonce, Uri url)
    {
        try
        {
            var outer = new AsnReader(response, AsnEncodingRules.BER).ReadSequence();
            var statusInfo = outer.ReadSequence();
            var status = (int)statusInfo.ReadInteger();
            if (status is not 0 and not 1)
            {
                var details = new List<string>();
                if (statusInfo.HasData && statusInfo.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
                {
                    var texts = statusInfo.ReadSequence();
                    while (texts.HasData)
                    {
                        details.Add(texts.ReadCharacterString(UniversalTagNumber.UTF8String));
                    }
                }

                if (statusInfo.HasData)
                {
                    var failInfo = statusInfo.ReadBitString(out _);
                    details.Add($"failInfo {Convert.ToHexString(failInfo)}");
                }

                var suffix = details.Count > 0 ? $" ({string.Join("; ", details)})" : string.Empty;
                throw new SigningException($"The timestamp authority {url} rejected the request with status {status}{suffix}.");
            }

            if (!outer.HasData)
            {
                throw new SigningException($"The timestamp authority {url} granted the request but sent no token.");
            }

            var token = outer.ReadEncodedValue().ToArray();
            CheckToken(token, imprint, nonce, url);
            return token;
        }
        catch (AsnContentException ex)
        {
            throw new SigningException($"The timestamp authority {url} sent a malformed response: {ex.Message}", ex);
        }
    }

    private static void CheckToken(byte[] token, ReadOnlySpan<byte> imprint, BigInteger nonce, Uri url)
    {
        var contentInfo = new AsnReader(token, AsnEncodingRules.BER).ReadSequence();
        if (contentInfo.ReadObjectIdentifier() != Oids.SignedData)
        {
            throw new SigningException($"The timestamp token from {url} is not CMS SignedData.");
        }

        var signedData = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        signedData.ReadInteger();
        signedData.ReadSetOf();
        var encapsulated = signedData.ReadSequence();
        if (encapsulated.ReadObjectIdentifier() != Oids.TstInfo)
        {
            throw new SigningException($"The timestamp token from {url} does not contain TSTInfo.");
        }

        var tstInfoBytes = encapsulated.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadOctetString();
        var tstInfo = new AsnReader(tstInfoBytes, AsnEncodingRules.BER).ReadSequence();
        tstInfo.ReadInteger();
        tstInfo.ReadObjectIdentifier();
        var messageImprint = tstInfo.ReadSequence();
        var algorithm = messageImprint.ReadSequence().ReadObjectIdentifier();
        var hashed = messageImprint.ReadOctetString();
        if (algorithm != Oids.Sha256 || !hashed.AsSpan().SequenceEqual(imprint))
        {
            throw new SigningException($"The timestamp token from {url} is for different data than requested.");
        }

        tstInfo.ReadInteger(); // serialNumber
        tstInfo.ReadGeneralizedTime();
        if (tstInfo.HasData && tstInfo.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
        {
            tstInfo.ReadSequence(); // accuracy
        }

        if (tstInfo.HasData && tstInfo.PeekTag().HasSameClassAndValue(Asn1Tag.Boolean))
        {
            tstInfo.ReadBoolean(); // ordering
        }

        if (!tstInfo.HasData || !tstInfo.PeekTag().HasSameClassAndValue(Asn1Tag.Integer) || tstInfo.ReadInteger() != nonce)
        {
            throw new SigningException($"The timestamp token from {url} does not carry the requested nonce.");
        }
    }

    private static void WriteMessageImprint(AsnWriter writer, ReadOnlySpan<byte> imprint)
    {
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(Oids.Sha256);
                writer.WriteNull();
            }

            writer.WriteOctetString(imprint);
        }
    }

    private static BigInteger CreateNonce()
    {
        Span<byte> bytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(bytes);
        return new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
    }
}
