using System.Text;

namespace Zexoro.Updates.Tests;

public sealed class FeedAddTests
{
    private static readonly DateTimeOffset Later = TestFeeds.Published.AddHours(1);

    [Fact]
    public void Add_NewVersionIncreasesSequence()
    {
        var feed = TestFeeds.Feed(5, TestFeeds.Release("1.0.0")).Add(TestFeeds.Release("1.1.0"), Later);
        Assert.Equal(6, feed.Sequence);
        Assert.Equal(Later, feed.Published);
        Assert.Equal(["1.1.0", "1.0.0"], feed.Releases.Select(r => r.Version.ToString()));
    }

    [Fact]
    public void Add_SameVersionMergesArtifacts()
    {
        var feed = TestFeeds.Feed(5, TestFeeds.Release("1.0.0", null, TestFeeds.Artifact()))
            .Add(TestFeeds.Release("1.0.0", "0.3.0", TestFeeds.Artifact(TestFeeds.MachineX64, file: "m.exe")), Later);
        Assert.Equal(6, feed.Sequence);
        var release = Assert.Single(feed.Releases);
        Assert.Equal(2, release.Artifacts.Count);
        Assert.Equal("0.3.0", release.MinimumUpdater?.ToString());
    }

    [Fact]
    public void Add_SameArtifactAgainChangesNothing()
    {
        var feed = TestFeeds.Feed(5, TestFeeds.Release("1.0.0", null, TestFeeds.Artifact()));
        Assert.Same(feed, feed.Add(TestFeeds.Release("1.0.0", null, TestFeeds.Artifact()), Later));
    }

    [Fact]
    public void Add_DifferentFileForPublishedTargetFails()
    {
        var feed = TestFeeds.Feed(5, TestFeeds.Release("1.0.0", null, TestFeeds.Artifact()));
        var ex = Assert.Throws<UpdateException>(() => feed.Add(TestFeeds.Release("1.0.0", null, TestFeeds.Artifact(sha256: new string('b', 64))), Later));
        Assert.Contains("publish a new version", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_SamePrecedenceDifferentBuildFails()
    {
        var feed = TestFeeds.Feed(5, TestFeeds.Release("1.0.0+a"));
        var ex = Assert.Throws<UpdateException>(() => feed.Add(TestFeeds.Release("1.0.0+b"), Later));
        Assert.Contains("same precedence", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fragment_StartsANewFeed()
    {
        var fragment = new ReleaseFragment(TestFeeds.App, "beta", TestFeeds.Release("2.0.0-rc.1"));
        var feed = fragment.AddTo(null, Later);
        Assert.Equal(1, feed.Sequence);
        Assert.Equal("beta", feed.Channel);
        Assert.Equal(TestFeeds.App, feed.App);
    }

    [Theory]
    [InlineData("com.example.other", TestFeeds.Channel)]
    [InlineData(TestFeeds.App, "beta")]
    public void Fragment_RejectsAnotherAppOrChannel(string app, string channel)
    {
        var fragment = new ReleaseFragment(app, channel, TestFeeds.Release("2.0.0"));
        Assert.Throws<UpdateException>(() => fragment.AddTo(TestFeeds.Feed(1, TestFeeds.Release("1.0.0")), Later));
    }

    [Fact]
    public void Fragment_RoundTrips()
    {
        var fragment = new ReleaseFragment(TestFeeds.App, TestFeeds.Channel, TestFeeds.Release("1.2.3", "0.2.0"));
        var json = fragment.ToJson();
        Assert.StartsWith("{\n  \"schema\": 1,", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
        var parsed = ReleaseFragment.Parse(json);
        Assert.Equal("1.2.3", parsed.Release.Version.ToString());
        Assert.Equal(json, parsed.ToJson());
    }

    [Fact]
    public void Fragment_RejectsNewerSchemaAndMissingRelease()
    {
        Assert.Equal(UpdateErrorKind.UpdaterTooOld, Assert.Throws<UpdateException>(() => ReleaseFragment.Parse("""{"schema":9}"""u8)).Kind);
        var ex = Assert.Throws<UpdateException>(() => ReleaseFragment.Parse("""{"schema":1,"app":"com.example.a","channel":"stable"}"""u8));
        Assert.Contains("'release' is missing", ex.Message, StringComparison.Ordinal);
    }
}
