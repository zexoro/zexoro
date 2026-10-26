using System.Text;

namespace Zexoro.Updates.Tests;

public sealed class UpdateCheckTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-updates-");
    private readonly UpdateSigningKey _key = UpdateSigningKey.Create();

    public void Dispose()
    {
        _key.Dispose();
        _dir.Delete(recursive: true);
    }

    private FeedSequenceStore Store => new(Path.Combine(_dir.FullName, "state.json"));

    private InstalledApp Installed(string version = "1.0.0", string channel = TestFeeds.Channel, params UpdatePublicKey[] keys) =>
        new(TestFeeds.App, channel, TestFeeds.V(version), TestFeeds.UserX64, new Uri("https://example.com/feed.json"), keys.Length == 0 ? [_key.PublicKey] : keys);

    private (byte[] Feed, byte[] Signature) Signed(UpdateFeed feed, UpdateSigningKey? key = null)
    {
        var json = feed.ToJson();
        return (json, FeedSignature.Sign(json, key ?? _key));
    }

    private UpdateSelection Evaluate(InstalledApp installed, (byte[] Feed, byte[] Signature) signed) =>
        UpdateCheck.Evaluate(installed, signed.Feed, signed.Signature, Store, TestFeeds.V("1.0.0"));

    [Fact]
    public void Evaluate_FindsAnUpdate()
    {
        var selection = Evaluate(Installed(), Signed(TestFeeds.Feed(3, TestFeeds.Release("1.1.0"))));
        Assert.Equal(UpdateStatus.Available, selection.Status);
        Assert.Equal(3, Store.GetSequence(TestFeeds.App, TestFeeds.Channel));
    }

    [Fact]
    public void Evaluate_VerifiesBeforeParsing()
    {
        // Garbage that is not JSON at all is reported as a signature failure: it is never parsed.
        var garbage = Encoding.UTF8.GetBytes("{\"schema\": 99");
        var ex = Assert.Throws<UpdateException>(() => Evaluate(Installed(), (garbage, FeedSignature.Sign(TestFeeds.Utf8("other"), _key))));
        Assert.Equal(UpdateErrorKind.Signature, ex.Kind);
    }

    [Fact]
    public void Evaluate_RejectsATamperedFeed()
    {
        var (feed, signature) = Signed(TestFeeds.Feed(3, TestFeeds.Release("1.1.0")));
        var text = Encoding.UTF8.GetString(feed).Replace("1.1.0", "9.1.0", StringComparison.Ordinal);
        var ex = Assert.Throws<UpdateException>(() => Evaluate(Installed(), (Encoding.UTF8.GetBytes(text), signature)));
        Assert.Equal(UpdateErrorKind.Signature, ex.Kind);
        Assert.Null(Store.GetSequence(TestFeeds.App, TestFeeds.Channel));
    }

    [Fact]
    public void Evaluate_RejectsAnUntrustedKey()
    {
        using var attacker = UpdateSigningKey.Create();
        var ex = Assert.Throws<UpdateException>(() => Evaluate(Installed(), Signed(TestFeeds.Feed(3, TestFeeds.Release("1.1.0")), attacker)));
        Assert.Equal(UpdateErrorKind.Signature, ex.Kind);
    }

    [Fact]
    public void Evaluate_AcceptsARotatedKeyFromTheTrustedList()
    {
        using var next = UpdateSigningKey.Create();
        var selection = Evaluate(Installed(keys: [_key.PublicKey, next.PublicKey]), Signed(TestFeeds.Feed(3, TestFeeds.Release("1.1.0")), next));
        Assert.Equal(UpdateStatus.Available, selection.Status);
    }

    [Fact]
    public void Evaluate_RejectsAReplayedOlderFeed()
    {
        var old = Signed(TestFeeds.Feed(3, TestFeeds.Release("1.1.0")));
        var current = Signed(TestFeeds.Feed(4, TestFeeds.Release("1.1.0"), TestFeeds.Release("1.1.1")));
        Assert.Equal("1.1.1", Evaluate(Installed(), current).Release?.Version.ToString());
        var ex = Assert.Throws<UpdateException>(() => Evaluate(Installed(), old));
        Assert.Equal(UpdateErrorKind.Replay, ex.Kind);
    }

    [Fact]
    public void Evaluate_RejectsAnotherAppOrChannelSignedWithTheSameKey()
    {
        var beta = Signed(new UpdateFeed(TestFeeds.App, "beta", 9, TestFeeds.Published, [TestFeeds.Release("2.0.0-beta.1")]));
        Assert.Equal(UpdateErrorKind.InvalidFeed, Assert.Throws<UpdateException>(() => Evaluate(Installed(), beta)).Kind);
        var other = Signed(new UpdateFeed("com.example.other", TestFeeds.Channel, 9, TestFeeds.Published, [TestFeeds.Release("2.0.0")]));
        Assert.Equal(UpdateErrorKind.InvalidFeed, Assert.Throws<UpdateException>(() => Evaluate(Installed(), other)).Kind);
        Assert.Null(Store.GetSequence(TestFeeds.App, TestFeeds.Channel));
    }

    [Fact]
    public void Evaluate_RejectsASignedFeedWithANewerSchema()
    {
        var json = TestFeeds.Utf8("""{"schema": 2, "app": "com.example.notes"}""");
        var ex = Assert.Throws<UpdateException>(() => Evaluate(Installed(), (json, FeedSignature.Sign(json, _key))));
        Assert.Equal(UpdateErrorKind.UpdaterTooOld, ex.Kind);
    }

    [Fact]
    public void Verify_RequiresAtLeastOneKey()
    {
        var (feed, signature) = Signed(TestFeeds.Feed(1));
        Assert.Equal(UpdateErrorKind.Signature, Assert.Throws<UpdateException>(() => FeedVerifier.Verify(feed, signature, [])).Kind);
    }
}
