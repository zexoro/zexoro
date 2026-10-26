using System.Buffers.Binary;
using Zexoro.Signing.Fixtures;

namespace Zexoro.Signing.Tests;

public class PeImageTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Read_UnsignedImage_FindsHeaderFields(bool pe32Plus)
    {
        var bytes = TestPe.Build(pe32Plus, overlayLength: 5);

        var image = PeImage.Read(new MemoryStream(bytes));

        Assert.Equal(pe32Plus, image.IsPe32Plus);
        Assert.Equal(TestPe.CheckSumOffset, image.CheckSumOffset);
        Assert.Equal(TestPe.CertificateEntryOffset(pe32Plus), image.CertificateTableEntryOffset);
        Assert.Equal(TestPe.SizeOfHeaders, image.SizeOfHeaders);
        Assert.False(image.HasCertificateTable);
        Assert.Equal(bytes.Length, image.SignedDataEnd);
        Assert.Equal([".text", ".rdata"], image.Sections.Select(s => s.Name));
    }

    [Fact]
    public void Read_CertificateTableAtEnd_ReportsIt()
    {
        var bytes = TestPe.Build(pe32Plus: true).Concat(new byte[16]).ToArray();
        var entry = TestPe.CertificateEntryOffset(pe32Plus: true);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry), bytes.Length - 16);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry + 4), 16);

        var image = PeImage.Read(new MemoryStream(bytes));

        Assert.True(image.HasCertificateTable);
        Assert.Equal(bytes.Length - 16, image.CertificateTableOffset);
        Assert.Equal(bytes.Length - 16, image.SignedDataEnd);
    }

    [Fact]
    public void Read_DataAfterCertificateTable_Throws()
    {
        var bytes = TestPe.Build(pe32Plus: true).Concat(new byte[24]).ToArray();
        var entry = TestPe.CertificateEntryOffset(pe32Plus: true);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry), bytes.Length - 24);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(entry + 4), 16);

        var ex = Assert.Throws<SigningException>(() => PeImage.Read(new MemoryStream(bytes)));
        Assert.Contains("after the certificate table", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_NotAnExecutable_Throws()
    {
        var bytes = "#!/bin/sh\necho this is not a PE file, but it is long enough to have a DOS header\n"u8.ToArray();

        var ex = Assert.Throws<SigningException>(() => PeImage.Read(new MemoryStream(bytes)));
        Assert.Contains("MZ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_TruncatedFile_Throws()
    {
        var bytes = TestPe.Build(pe32Plus: true).AsSpan(0, 0x100).ToArray();

        Assert.Throws<SigningException>(() => PeImage.Read(new MemoryStream(bytes)));
    }

    [Fact]
    public void Read_UnknownOptionalHeaderMagic_Throws()
    {
        var bytes = TestPe.Build(pe32Plus: true);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(TestPe.OptionalHeaderOffset), 0x107);

        var ex = Assert.Throws<SigningException>(() => PeImage.Read(new MemoryStream(bytes)));
        Assert.Contains("0x0107", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_TooFewDataDirectories_Throws()
    {
        var bytes = TestPe.Build(pe32Plus: false);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(TestPe.OptionalHeaderOffset + 92), 4);

        Assert.Throws<SigningException>(() => PeImage.Read(new MemoryStream(bytes)));
    }
}
