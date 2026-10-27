using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Zexoro.Signing.Fixtures;

namespace Zexoro.Signing.Tests;

public partial class PeChecksumTests
{
    [Fact]
    public void Compute_SmallInput_SumsWordsPlusLength()
    {
        // 0x0201 + 0x0403 + 0x0005 (odd trailing byte) = 0x0609, plus the length 5.
        var checksum = PeChecksum.Compute(new MemoryStream([1, 2, 3, 4, 5]), checkSumOffset: 100);

        Assert.Equal(0x060Eu, checksum);
    }

    [Fact]
    public void Compute_Overflow_FoldsCarries()
    {
        // 0xFFFF + 0xFFFF = 0x1FFFE, folded to 0xFFFF, plus the length 4.
        var checksum = PeChecksum.Compute(new MemoryStream([0xFF, 0xFF, 0xFF, 0xFF]), checkSumOffset: 100);

        Assert.Equal(0x10003u, checksum);
    }

    [Fact]
    public void Compute_ChecksumField_CountsAsZero()
    {
        var bytes = TestPe.Build(pe32Plus: true);
        var before = PeChecksum.Compute(new MemoryStream(bytes), TestPe.CheckSumOffset);

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(TestPe.CheckSumOffset), 0x12345678);

        Assert.Equal(before, PeChecksum.Compute(new MemoryStream(bytes), TestPe.CheckSumOffset));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(200_001)]
    public void Compute_LargeImages_MatchesReference(int overlayLength)
    {
        var bytes = TestPe.Build(pe32Plus: false, overlayLength, seed: 3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(TestPe.CheckSumOffset), 0xFFFFFFFF);

        Assert.Equal(ReferenceChecksum(bytes, TestPe.CheckSumOffset), PeChecksum.Compute(new MemoryStream(bytes), TestPe.CheckSumOffset));
    }

    [Fact]
    public void SignFile_SignedImage_StoresItsChecksum()
    {
        using var file = new TempFile(TestPe.Build(pe32Plus: true, overlayLength: 9));
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa: false);
        using var credential = new SigningCredential(certificate);

        AuthenticodeSigner.SignFile(file.Path, new SigningOptions { Credential = credential });

        var signed = file.ReadAllBytes();
        Assert.NotEqual(0u, SignedImage.CheckSumField(signed));
        Assert.Equal(ReferenceChecksum(signed, TestPe.CheckSumOffset), SignedImage.CheckSumField(signed));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 7)]
    [InlineData(true, 100_001)]
    public void Compute_SameAsWindowsCheckSumMappedFile(bool pe32Plus, int overlayLength)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "CheckSumMappedFile is a Windows API.");

        var bytes = TestPe.Build(pe32Plus, overlayLength, seed: 11);
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        uint windows;
        try
        {
            var headers = CheckSumMappedFile(handle.AddrOfPinnedObject(), (uint)bytes.Length, out _, out windows);
            Assert.NotEqual(IntPtr.Zero, headers);
        }
        finally
        {
            handle.Free();
        }

        Assert.Equal(windows, PeChecksum.Compute(new MemoryStream(bytes), TestPe.CheckSumOffset));
    }

    /// <summary>The straightforward word-by-word algorithm, folding after every addition.</summary>
    private static uint ReferenceChecksum(byte[] bytes, int checkSumOffset)
    {
        uint sum = 0;
        for (var i = 0; i < bytes.Length; i += 2)
        {
            uint word = i + 1 < bytes.Length ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i)) : bytes[i];
            if (i >= checkSumOffset && i < checkSumOffset + 4)
            {
                word = 0;
            }

            sum += word;
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return sum + (uint)bytes.Length;
    }

    [LibraryImport("imagehlp.dll", SetLastError = true)]
    private static partial IntPtr CheckSumMappedFile(IntPtr baseAddress, uint fileLength, out uint headerSum, out uint checkSum);
}
