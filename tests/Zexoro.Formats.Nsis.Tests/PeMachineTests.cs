using Zexoro.Platforms;
using Zexoro.Signing.Fixtures;

namespace Zexoro.Formats.Nsis.Tests;

public sealed class PeMachineTests : IDisposable
{
    private readonly string _path = Path.GetTempFileName();

    public void Dispose() => File.Delete(_path);

    [Theory]
    [InlineData(true, "x64")]
    [InlineData(false, "x86")]
    public void Read_ReturnsArchitecture(bool pe32Plus, string expected)
    {
        File.WriteAllBytes(_path, TestPe.Build(pe32Plus));
        Assert.Equal(Architecture.Parse(expected), PeMachine.Read(_path));
    }

    [Fact]
    public void Read_Arm64()
    {
        var image = TestPe.Build(pe32Plus: true);
        image[TestPe.PeHeaderOffset + 4] = 0x64;
        image[TestPe.PeHeaderOffset + 5] = 0xAA;
        File.WriteAllBytes(_path, image);
        Assert.Equal(Architecture.Arm64, PeMachine.Read(_path));
    }

    [Theory]
    [InlineData(new byte[] { 0x4D, 0x5A })]
    [InlineData(new byte[] { 1, 2, 3 })]
    public void Read_NotPe_ReturnsNull(byte[] content)
    {
        File.WriteAllBytes(_path, content);
        Assert.Null(PeMachine.Read(_path));
    }

    [Fact]
    public void Read_UnsupportedMachine_ReturnsNull()
    {
        var image = TestPe.Build(pe32Plus: false);
        image[TestPe.PeHeaderOffset + 4] = 0xC4;
        image[TestPe.PeHeaderOffset + 5] = 0x01;
        File.WriteAllBytes(_path, image);
        Assert.Null(PeMachine.Read(_path));
    }
}
