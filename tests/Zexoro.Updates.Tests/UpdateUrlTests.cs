namespace Zexoro.Updates.Tests;

public sealed class UpdateUrlTests
{
    [Theory]
    [InlineData("https://example.com/feed.json", true)]
    [InlineData("HTTPS://EXAMPLE.COM/feed.json", true)]
    [InlineData("http://localhost:8080/feed.json", true)]
    [InlineData("http://LOCALHOST/feed.json", true)]
    [InlineData("http://127.0.0.1:5000/feed.json", true)]
    [InlineData("http://[::1]:5000/feed.json", true)]
    [InlineData("http://example.com/feed.json", false)]
    [InlineData("http://localhost.example.com/feed.json", false)]
    [InlineData("http://127.0.0.2/feed.json", false)]
    [InlineData("ftp://localhost/feed.json", false)]
    [InlineData("file:///tmp/feed.json", false)]
    [InlineData("/feed.json", false)]
    public void IsAllowed_FollowsTheHttpsRule(string url, bool allowed)
    {
        Assert.Equal(allowed, UpdateUrl.TryParse(url, out _));
    }

    [Fact]
    public void EnsureAllowed_ThrowsInsecureUrl()
    {
        var ex = Assert.Throws<UpdateException>(() => UpdateUrl.EnsureAllowed(new Uri("http://example.com/x")));
        Assert.Equal(UpdateErrorKind.InsecureUrl, ex.Kind);
    }

    [Fact]
    public void SignatureUrl_AppendsSigToThePath()
    {
        Assert.Equal("https://example.com/stable/feed.json.sig?x=1", UpdateClient.SignatureUrl(new Uri("https://example.com/stable/feed.json?x=1")).AbsoluteUri);
    }
}
