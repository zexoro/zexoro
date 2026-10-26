namespace Zexoro.Formats.Nsis.Tests;

public sealed class SignFileRequestTests : IDisposable
{
    private readonly string _path = Path.GetTempFileName();

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void WriteRead_RoundTrips()
    {
        var request = new SignFileRequest
        {
            Certificate = "/path with space/\"cert\".pfx",
            Timestamp = new Uri("http://timestamp.example.com/tsa"),
            Description = "Example $Notes",
            DescriptionUrl = new Uri("https://example.com/"),
        };
        request.Write(_path);
        Assert.Equal(request, SignFileRequest.Read(_path));
        Assert.DoesNotContain("password", File.ReadAllText(_path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_Minimal()
    {
        File.WriteAllText(_path, """{ "certificate": "c.pfx" }""");
        var request = SignFileRequest.Read(_path);
        Assert.Equal("c.pfx", request.Certificate);
        Assert.Null(request.Timestamp);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "certificate": "c.pfx", "timestamp": "relative" }""")]
    public void Read_Invalid_Throws(string text)
    {
        File.WriteAllText(_path, text);
        Assert.Throws<InvalidDataException>(() => SignFileRequest.Read(_path));
    }
}
