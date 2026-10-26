using System.Net;
using System.Security.Cryptography;

namespace Zexoro.Updates.Tests;

public sealed class UpdateClientTests : IDisposable
{
    private static readonly Uri FeedUrl = new("https://example.com/stable/feed.json");

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-updates-");
    private readonly UpdateSigningKey _key = UpdateSigningKey.Create();
    private readonly FakeHandler _handler = new();
    private readonly HttpClient _http;

    public UpdateClientTests() => _http = new HttpClient(_handler);

    public void Dispose()
    {
        _http.Dispose();
        _key.Dispose();
        _dir.Delete(recursive: true);
    }

    private UpdateClient Client => new(_http);

    private string Destination => Path.Combine(_dir.FullName, "downloads", "app.exe");

    private static FeedArtifact ArtifactFor(byte[] content, Uri? url = null) =>
        new(TestFeeds.UserX64, url ?? new Uri("https://example.com/dl/app.exe"), content.Length, Convert.ToHexStringLower(SHA256.HashData(content)));

    private InstalledApp Installed => new(TestFeeds.App, TestFeeds.Channel, TestFeeds.V("1.0.0"), TestFeeds.UserX64, FeedUrl, [_key.PublicKey]);

    [Fact]
    public async Task Check_FetchesFeedAndSignature()
    {
        var json = TestFeeds.Feed(2, TestFeeds.Release("1.1.0")).ToJson();
        _handler.Responses[FeedUrl.AbsoluteUri] = json;
        _handler.Responses[FeedUrl.AbsoluteUri + ".sig"] = FeedSignature.Sign(json, _key);
        var store = new FeedSequenceStore(Path.Combine(_dir.FullName, "state.json"));
        var selection = await Client.CheckAsync(Installed, store, TestFeeds.V("1.0.0"), TestContext.Current.CancellationToken);
        Assert.Equal(UpdateStatus.Available, selection.Status);
        Assert.Equal(2, store.GetSequence(TestFeeds.App, TestFeeds.Channel));
    }

    [Fact]
    public async Task Check_ReportsHttpErrorsAsNetwork()
    {
        var store = new FeedSequenceStore(Path.Combine(_dir.FullName, "state.json"));
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Client.CheckAsync(Installed, store, TestFeeds.V("1.0.0"), TestContext.Current.CancellationToken));
        Assert.Equal(UpdateErrorKind.Network, ex.Kind);
        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_RefusesPlainHttpFeeds()
    {
        var store = new FeedSequenceStore(Path.Combine(_dir.FullName, "state.json"));
        var installed = Installed with { Feed = new Uri("http://example.com/feed.json") };
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Client.CheckAsync(installed, store, TestFeeds.V("1.0.0"), TestContext.Current.CancellationToken));
        Assert.Equal(UpdateErrorKind.InsecureUrl, ex.Kind);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Check_RejectsOversizedFeeds()
    {
        _handler.Responses[FeedUrl.AbsoluteUri] = new byte[UpdateClient.MaxFeedSize + 1];
        var store = new FeedSequenceStore(Path.Combine(_dir.FullName, "state.json"));
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Client.CheckAsync(Installed, store, TestFeeds.V("1.0.0"), TestContext.Current.CancellationToken));
        Assert.Contains("larger than", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_WritesAVerifiedFile()
    {
        byte[] content = [1, 2, 3, 4, 5];
        var artifact = ArtifactFor(content);
        _handler.Responses[artifact.Url.AbsoluteUri] = content;
        await Client.DownloadAsync(artifact, Destination, TestContext.Current.CancellationToken);
        Assert.Equal(content, await File.ReadAllBytesAsync(Destination, TestContext.Current.CancellationToken));
        UpdateClient.VerifyFile(Destination, artifact);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3, 4, 6 }, "SHA-256")]
    [InlineData(new byte[] { 1, 2, 3, 4 }, "4 bytes")]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6 }, "larger than")]
    public async Task Download_RejectsMismatchedContent(byte[] served, string message)
    {
        var artifact = ArtifactFor([1, 2, 3, 4, 5]);
        _handler.Responses[artifact.Url.AbsoluteUri] = served;
        _handler.OmitContentLength = true;
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Client.DownloadAsync(artifact, Destination, TestContext.Current.CancellationToken));
        Assert.Equal(UpdateErrorKind.Integrity, ex.Kind);
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
    }

    [Fact]
    public async Task Download_RejectsAWrongContentLengthEarly()
    {
        var artifact = ArtifactFor([1, 2, 3, 4, 5]);
        _handler.Responses[artifact.Url.AbsoluteUri] = [1, 2, 3];
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Client.DownloadAsync(artifact, Destination, TestContext.Current.CancellationToken));
        Assert.Contains("announces 3 bytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_KeepsAnExistingFileWhenVerificationFails()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
        await File.WriteAllBytesAsync(Destination, [9], TestContext.Current.CancellationToken);
        var artifact = ArtifactFor([1, 2, 3]);
        _handler.Responses[artifact.Url.AbsoluteUri] = [1, 2, 4];
        await Assert.ThrowsAsync<UpdateException>(() => Client.DownloadAsync(artifact, Destination, TestContext.Current.CancellationToken));
        Assert.Equal([9], await File.ReadAllBytesAsync(Destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Download_RejectsARedirectToPlainHttp()
    {
        byte[] content = [1, 2, 3];
        var artifact = ArtifactFor(content);
        _handler.Responses[artifact.Url.AbsoluteUri] = content;
        _handler.FinalUrl = new Uri("http://mirror.example.com/app.exe");
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Client.DownloadAsync(artifact, Destination, TestContext.Current.CancellationToken));
        Assert.Equal(UpdateErrorKind.InsecureUrl, ex.Kind);
    }

    [Fact]
    public void VerifyStream_RejectsTamperedBytes()
    {
        var artifact = ArtifactFor([1, 2, 3]);
        UpdateClient.VerifyStream(new MemoryStream([1, 2, 3]), artifact);
        Assert.Equal(UpdateErrorKind.Integrity, Assert.Throws<UpdateException>(() => UpdateClient.VerifyStream(new MemoryStream([1, 2, 2]), artifact)).Kind);
        Assert.Equal(UpdateErrorKind.Integrity, Assert.Throws<UpdateException>(() => UpdateClient.VerifyStream(new MemoryStream([1, 2, 3, 4]), artifact)).Kind);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Responses { get; } = new(StringComparer.Ordinal);

        public List<Uri> Requests { get; } = [];

        public bool OmitContentLength { get; set; }

        public Uri? FinalUrl { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (FinalUrl is not null)
            {
                request.RequestUri = FinalUrl;
            }

            if (!Responses.TryGetValue(Requests[^1].AbsoluteUri, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            }

            HttpContent content = OmitContentLength ? new StreamContent(new UnknownLengthStream(body)) : new ByteArrayContent(body);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }

    /// <summary>A stream that cannot report its length, so the response has no Content-Length.</summary>
    private sealed class UnknownLengthStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }
}
