using System.Buffers.Binary;
using System.Net;
using Zexoro.Signing.Fixtures;

namespace Zexoro.Signing.Tests;

public class AuthenticodeSignerTests
{
    [Theory]
    [InlineData(true, 13)]
    [InlineData(false, 0)]
    public void SignFile_UnsignedImage_AppendsAlignedCertificateTable(bool pe32Plus, int overlayLength)
    {
        var original = TestPe.Build(pe32Plus, overlayLength);
        using var file = new TempFile(original);
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa: true);
        using var credential = new SigningCredential(certificate);
        Assert.False(AuthenticodeSigner.IsSigned(file.Path));

        AuthenticodeSigner.SignFile(file.Path, new SigningOptions { Credential = credential });

        var signed = file.ReadAllBytes();
        var (offset, size) = SignedImage.CertificateTable(signed, pe32Plus);
        Assert.Equal(original.Length + AuthenticodeDigest.PaddingTo8(original.Length), offset);
        Assert.Equal(0, offset % 8);
        Assert.Equal(0, size % 8);
        Assert.Equal(signed.Length, offset + size);
        Assert.True(signed.AsSpan(original.Length, offset - original.Length).IndexOfAnyExcept((byte)0) < 0);
        SignedImage.SignedData(signed, pe32Plus);
        Assert.True(AuthenticodeSigner.IsSigned(file.Path));
    }

    [Fact]
    public void SignFile_UnsignedImage_ChangesOnlyChecksumAndCertificateEntry()
    {
        var original = TestPe.Build(pe32Plus: true, overlayLength: 16);
        using var file = new TempFile(original);
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa: false);
        using var credential = new SigningCredential(certificate);

        AuthenticodeSigner.SignFile(file.Path, new SigningOptions { Credential = credential });

        var signed = file.ReadAllBytes().AsSpan(0, original.Length).ToArray();
        var entry = TestPe.CertificateEntryOffset(pe32Plus: true);
        for (var i = 0; i < original.Length; i++)
        {
            var excluded = (i >= TestPe.CheckSumOffset && i < TestPe.CheckSumOffset + 4) || (i >= entry && i < entry + 8);
            if (!excluded)
            {
                Assert.True(original[i] == signed[i], $"byte {i} changed");
            }
        }
    }

    [Fact]
    public void SignFile_SignedImage_ReplacesExistingSignature()
    {
        var original = TestPe.Build(pe32Plus: true, overlayLength: 5);
        using var file = new TempFile(original);
        using var first = TestCertificates.CreateSelfSigned(ecdsa: false, "first signer");
        using var second = TestCertificates.CreateSelfSigned(ecdsa: true, "second signer");
        using var firstCredential = new SigningCredential(first);
        using var secondCredential = new SigningCredential(second);
        using var reference = new TempFile(original);

        AuthenticodeSigner.SignFile(file.Path, new SigningOptions { Credential = firstCredential });
        AuthenticodeSigner.SignFile(file.Path, new SigningOptions { Credential = secondCredential });
        AuthenticodeSigner.SignFile(reference.Path, new SigningOptions { Credential = secondCredential });

        var resigned = file.ReadAllBytes();
        var (offset, size) = SignedImage.CertificateTable(resigned, pe32Plus: true);
        Assert.Equal(SignedImage.CertificateTable(reference.ReadAllBytes(), pe32Plus: true).Offset, offset);
        Assert.Equal(resigned.Length, offset + size);
        var signature = SignedDataTests.Parse(SignedImage.SignedData(resigned, pe32Plus: true));
        Assert.Equal([second.RawData], signature.Certificates);
        Assert.Equal(AuthenticodeSigner.ComputeDigest(new MemoryStream(original)), signature.ImageDigest);
        var expected = reference.ReadAllBytes();
        Assert.Equal(expected.AsSpan(0, TestPe.CheckSumOffset).ToArray(), resigned.AsSpan(0, TestPe.CheckSumOffset).ToArray());
        Assert.Equal(expected[(TestPe.CheckSumOffset + 4)..offset], resigned[(TestPe.CheckSumOffset + 4)..offset]);
    }

    [Fact]
    public void SignFile_NotAPeFile_ThrowsAndLeavesFileUnchanged()
    {
        var contents = new byte[512];
        contents[0] = (byte)'M';
        contents[1] = (byte)'Z';
        using var file = new TempFile(contents);
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa: true);
        using var credential = new SigningCredential(certificate);

        Assert.Throws<SigningException>(() => AuthenticodeSigner.SignFile(file.Path, new SigningOptions { Credential = credential }));
        Assert.Equal(contents, file.ReadAllBytes());
    }

    [Fact]
    public void SignFile_MissingFile_Throws()
    {
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa: true);
        using var credential = new SigningCredential(certificate);
        var path = Path.Combine(Path.GetTempPath(), $"zexoro-missing-{Guid.NewGuid():N}.exe");

        var ex = Assert.Throws<SigningException>(() => AuthenticodeSigner.SignFile(path, new SigningOptions { Credential = credential }));
        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SignFile_TimestampNetworkFailure_ThrowsClearErrorAndLeavesFileUnchanged()
    {
        using var authority = new FakeTimestampAuthority { Failure = new HttpRequestException("Connection refused") };
        var ex = SignWithTimestamp(authority, out var unchanged);

        Assert.Contains("Cannot reach the timestamp authority http://tsa.invalid/", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Connection refused", ex.Message, StringComparison.Ordinal);
        Assert.True(unchanged);
    }

    [Fact]
    public void SignFile_TimestampTimeout_ThrowsClearError()
    {
        using var authority = new FakeTimestampAuthority { Failure = new TaskCanceledException() };
        var ex = SignWithTimestamp(authority, out var unchanged);

        Assert.Contains("timed out", ex.Message, StringComparison.Ordinal);
        Assert.True(unchanged);
    }

    [Fact]
    public void SignFile_TimestampHttpError_ThrowsClearError()
    {
        using var authority = new FakeTimestampAuthority { HttpStatus = HttpStatusCode.ServiceUnavailable };
        var ex = SignWithTimestamp(authority, out var unchanged);

        Assert.Contains("HTTP 503", ex.Message, StringComparison.Ordinal);
        Assert.True(unchanged);
    }

    [Fact]
    public void SignFile_TimestampRejected_ThrowsWithStatus()
    {
        using var authority = new FakeTimestampAuthority { Status = 2 };
        var ex = SignWithTimestamp(authority, out var unchanged);

        Assert.Contains("rejected the request with status 2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("fake rejection", ex.Message, StringComparison.Ordinal);
        Assert.True(unchanged);
    }

    [Fact]
    public void SignFile_TimestampNonceMismatch_Throws()
    {
        using var authority = new FakeTimestampAuthority { NonceOverride = 12345 };
        var ex = SignWithTimestamp(authority, out var unchanged);

        Assert.Contains("nonce", ex.Message, StringComparison.Ordinal);
        Assert.True(unchanged);
    }

    [Fact]
    public void SignFile_GrantedWithModifications_AcceptsToken()
    {
        using var authority = new FakeTimestampAuthority { Status = 1 };
        using var client = new HttpClient(authority);
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa: true);
        using var credential = new SigningCredential(certificate);
        using var file = new TempFile(TestPe.Build(pe32Plus: true));

        AuthenticodeSigner.SignFile(file.Path, new SigningOptions
        {
            Credential = credential,
            TimestampUrl = new Uri("http://tsa.invalid/"),
            HttpClient = client,
        });

        Assert.Equal(1, authority.Requests);
        Assert.True(AuthenticodeSigner.IsSigned(file.Path));
    }

    [Fact]
    public void IsSigned_CertificateTableOfOtherType_ReturnsFalse()
    {
        var bytes = TestPe.Build(pe32Plus: true).Concat(new byte[16]).ToArray();
        var entry = TestPe.CertificateEntryOffset(pe32Plus: true);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry), bytes.Length - 16);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry + 4), 16);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 12), 0x0200);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 10), 0x0001); // X.509, not PKCS #7
        using var file = new TempFile(bytes);

        Assert.False(AuthenticodeSigner.IsSigned(file.Path));
    }

    private static SigningException SignWithTimestamp(FakeTimestampAuthority authority, out bool unchanged)
    {
        var original = TestPe.Build(pe32Plus: true);
        using var client = new HttpClient(authority);
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa: false);
        using var credential = new SigningCredential(certificate);
        using var file = new TempFile(original);

        var ex = Assert.Throws<SigningException>(() => AuthenticodeSigner.SignFile(file.Path, new SigningOptions
        {
            Credential = credential,
            TimestampUrl = new Uri("http://tsa.invalid/"),
            HttpClient = client,
        }));

        unchanged = original.AsSpan().SequenceEqual(file.ReadAllBytes());
        return ex;
    }
}
