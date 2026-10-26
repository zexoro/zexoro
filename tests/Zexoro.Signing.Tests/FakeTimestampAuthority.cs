using System.Formats.Asn1;
using System.Net;
using System.Numerics;

namespace Zexoro.Signing.Tests;

/// <summary>An offline RFC 3161 responder: answers each request with a token for the requested imprint and nonce.</summary>
internal sealed class FakeTimestampAuthority : HttpMessageHandler
{
    public const string TstInfoOid = "1.2.840.113549.1.9.16.1.4";

    public int Status { get; init; }

    public HttpStatusCode HttpStatus { get; init; } = HttpStatusCode.OK;

    public Exception? Failure { get; init; }

    /// <summary>Replaces the nonce echoed in the token.</summary>
    public BigInteger? NonceOverride { get; init; }

    public byte[]? LastImprint { get; private set; }

    public byte[]? LastToken { get; private set; }

    public string? LastContentType { get; private set; }

    public int Requests { get; private set; }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (Failure is not null)
        {
            throw Failure;
        }

        Assert.Equal(HttpMethod.Post, request.Method);
        LastContentType = request.Content!.Headers.ContentType?.MediaType;
        using var body = new MemoryStream();
        request.Content.ReadAsStream(cancellationToken).CopyTo(body);

        // TimeStampReq ::= SEQUENCE { version, messageImprint, reqPolicy?, nonce?, certReq? }
        var req = new AsnReader(body.ToArray(), AsnEncodingRules.DER).ReadSequence();
        Assert.Equal(1, (int)req.ReadInteger());
        var messageImprint = req.ReadSequence();
        var algorithm = messageImprint.ReadSequence().ReadObjectIdentifier();
        LastImprint = messageImprint.ReadOctetString();
        var nonce = req.ReadInteger();
        Assert.True(req.ReadBoolean());
        Assert.False(req.HasData);

        var response = BuildResponse(algorithm, LastImprint, NonceOverride ?? nonce);
        return new HttpResponseMessage(HttpStatus) { Content = new ByteArrayContent(response) };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Send(request, cancellationToken));

    private byte[] BuildResponse(string algorithm, byte[] imprint, BigInteger nonce)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteInteger(Status);
                if (Status >= 2)
                {
                    using (writer.PushSequence())
                    {
                        writer.WriteCharacterString(UniversalTagNumber.UTF8String, "fake rejection");
                    }
                }
            }

            if (Status < 2)
            {
                LastToken = BuildToken(algorithm, imprint, nonce);
                writer.WriteEncodedValue(LastToken);
            }
        }

        return writer.Encode();
    }

    /// <summary>A structurally valid, unsigned token: SignedData with TSTInfo and no signers.</summary>
    private static byte[] BuildToken(string algorithm, byte[] imprint, BigInteger nonce)
    {
        var tstInfo = new AsnWriter(AsnEncodingRules.DER);
        using (tstInfo.PushSequence())
        {
            tstInfo.WriteInteger(1);
            tstInfo.WriteObjectIdentifier("1.2.3.4.5");
            using (tstInfo.PushSequence())
            {
                using (tstInfo.PushSequence())
                {
                    tstInfo.WriteObjectIdentifier(algorithm);
                    tstInfo.WriteNull();
                }

                tstInfo.WriteOctetString(imprint);
            }

            tstInfo.WriteInteger(42);
            tstInfo.WriteGeneralizedTime(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
            using (tstInfo.PushSequence())
            {
                tstInfo.WriteInteger(1);
            }

            tstInfo.WriteInteger(nonce);
        }

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier("1.2.840.113549.1.7.2");
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
            using (writer.PushSequence())
            {
                writer.WriteInteger(3);
                using (writer.PushSetOf())
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1");
                }

                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(TstInfoOid);
                    using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
                    {
                        writer.WriteOctetString(tstInfo.Encode());
                    }
                }

                using (writer.PushSetOf())
                {
                }
            }
        }

        return writer.Encode();
    }
}
