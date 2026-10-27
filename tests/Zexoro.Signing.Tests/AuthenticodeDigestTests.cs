using System.Buffers.Binary;
using System.Security.Cryptography;
using Zexoro.Signing.Fixtures;

namespace Zexoro.Signing.Tests;

public class AuthenticodeDigestTests
{
    // Calculated by osslsigncode 2.x ("Calculated message digest") for the same images.
    private const string Pe32PlusWithOverlayDigest = "990F28456106495DFFCC33AA45ECB940E88274EEDB709D507C59CDB13AB998F2";
    private const string Pe32Digest = "74F85E612A2DACFCFE7FC90A9EF4A218CCCA383A33982FF252A1F6D03B578502";

    [Fact]
    public void Compute_Pe32PlusWithUnalignedOverlay_MatchesOsslsigncode()
    {
        var digest = Digest(TestPe.Build(pe32Plus: true, overlayLength: 13));

        Assert.Equal(Pe32PlusWithOverlayDigest, Convert.ToHexString(digest));
    }

    [Fact]
    public void Compute_Pe32_MatchesOsslsigncode()
    {
        var digest = Digest(TestPe.Build(pe32Plus: false));

        Assert.Equal(Pe32Digest, Convert.ToHexString(digest));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 64)]
    [InlineData(false, 0)]
    [InlineData(false, 4096)]
    public void Compute_ContiguousSections_MatchesSpecificationProcedure(bool pe32Plus, int overlayLength)
    {
        var bytes = TestPe.Build(pe32Plus, overlayLength);

        Assert.Equal(SpecificationDigest(bytes, pe32Plus), Digest(bytes));
    }

    [Fact]
    public void Compute_ExcludedFieldsChange_DigestUnchanged()
    {
        var bytes = TestPe.Build(pe32Plus: true);
        var before = Digest(bytes);

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(TestPe.CheckSumOffset), 0xDEADBEEF);
        var entry = TestPe.CertificateEntryOffset(pe32Plus: true);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entry + 4), 0);

        Assert.Equal(before, Digest(bytes));
    }

    [Theory]
    [InlineData(0x40)] // DOS stub
    [InlineData(TestPe.CheckSumOffset - 8)] // SizeOfImage
    [InlineData(TestPe.CheckSumOffset + 4)]
    [InlineData(TestPe.SizeOfHeaders + 2)] // code
    [InlineData(TestPe.SizeOfHeaders + TestPe.SectionRawSize + 7)] // data
    public void Compute_CoveredByteChanges_DigestChanges(int offset)
    {
        var bytes = TestPe.Build(pe32Plus: true);
        var before = Digest(bytes);

        bytes[offset] ^= 0x5A;

        Assert.NotEqual(before, Digest(bytes));
    }

    [Fact]
    public void Compute_ByteNextToCertificateEntry_DigestChanges()
    {
        var bytes = TestPe.Build(pe32Plus: false);
        var before = Digest(bytes);

        bytes[TestPe.CertificateEntryOffset(pe32Plus: false) + 8] ^= 1;

        Assert.NotEqual(before, Digest(bytes));
    }

    [Fact]
    public void Compute_UnalignedFile_HashesZeroPaddingTo8Bytes()
    {
        var unaligned = TestPe.Build(pe32Plus: true, overlayLength: 3);
        var padded = unaligned.Concat(new byte[5]).ToArray();

        Assert.Equal(Digest(padded), Digest(unaligned));
    }

    [Fact]
    public void Compute_SignedFile_EqualsDigestBeforeSigning()
    {
        var bytes = TestPe.Build(pe32Plus: true, overlayLength: 13);
        using var file = new TempFile(bytes);
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa: true);
        using var credential = new SigningCredential(certificate);

        AuthenticodeSigner.SignFile(file.Path, new SigningOptions { Credential = credential });

        Assert.Equal(Pe32PlusWithOverlayDigest, Convert.ToHexString(Digest(file.ReadAllBytes())));
    }

    private static byte[] Digest(byte[] bytes) => AuthenticodeSigner.ComputeDigest(new MemoryStream(bytes));

    /// <summary>
    /// The procedure exactly as written in "Windows Authenticode Portable Executable Signature
    /// Format": headers without the checksum and certificate entry, sections ordered by
    /// PointerToRawData, then the remaining data up to the certificate table.
    /// </summary>
    private static byte[] SpecificationDigest(byte[] bytes, bool pe32Plus)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var checksum = TestPe.CheckSumOffset;
        var entry = TestPe.CertificateEntryOffset(pe32Plus);
        var optional = TestPe.OptionalHeaderOffset;
        var sizeOfHeaders = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(optional + 60));

        hash.AppendData(bytes, 0, checksum);
        hash.AppendData(bytes, checksum + 4, entry - (checksum + 4));
        hash.AppendData(bytes, entry + 8, sizeOfHeaders - (entry + 8));

        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(TestPe.PeHeaderOffset + 6));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(TestPe.PeHeaderOffset + 20));
        var sections = Enumerable.Range(0, sectionCount)
            .Select(i => optional + optionalSize + (i * 40))
            .Select(h => (Pointer: BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(h + 20)),
                          Size: BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(h + 16))))
            .Where(s => s.Size > 0)
            .OrderBy(s => s.Pointer);

        long sumOfBytesHashed = sizeOfHeaders;
        foreach (var (pointer, size) in sections)
        {
            hash.AppendData(bytes, pointer, size);
            sumOfBytesHashed += size;
        }

        var certificateSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(entry + 4));
        var fileSize = bytes.Length - certificateSize;
        hash.AppendData(bytes, (int)sumOfBytesHashed, (int)(fileSize - sumOfBytesHashed));
        return hash.GetHashAndReset();
    }
}
