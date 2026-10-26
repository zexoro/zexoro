namespace Zexoro.Updates.Tests;

public sealed class SequenceStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-updates-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string StatePath => Path.Combine(_dir.FullName, "state", "state.json");

    [Fact]
    public void Accept_PersistsTheHighestSequence()
    {
        var store = new FeedSequenceStore(StatePath);
        Assert.Null(store.GetSequence(TestFeeds.App, TestFeeds.Channel));
        store.Accept(TestFeeds.Feed(5));
        store.Accept(TestFeeds.Feed(7));
        Assert.Equal(7, new FeedSequenceStore(StatePath).GetSequence(TestFeeds.App, TestFeeds.Channel));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(StatePath)!));
    }

    [Fact]
    public void Accept_RejectsALowerSequence()
    {
        var store = new FeedSequenceStore(StatePath);
        store.Accept(TestFeeds.Feed(7));
        var ex = Assert.Throws<UpdateException>(() => new FeedSequenceStore(StatePath).Accept(TestFeeds.Feed(6)));
        Assert.Equal(UpdateErrorKind.Replay, ex.Kind);
        Assert.Equal(7, store.GetSequence(TestFeeds.App, TestFeeds.Channel));
    }

    [Fact]
    public void Accept_AllowsTheSameSequenceAgain()
    {
        var store = new FeedSequenceStore(StatePath);
        store.Accept(TestFeeds.Feed(7));
        store.Accept(TestFeeds.Feed(7));
        Assert.Equal(7, store.GetSequence(TestFeeds.App, TestFeeds.Channel));
    }

    [Fact]
    public void Accept_KeepsChannelsApart()
    {
        var store = new FeedSequenceStore(StatePath);
        store.Accept(TestFeeds.Feed(7));
        store.Accept(new UpdateFeed(TestFeeds.App, "beta", 2, TestFeeds.Published, []));
        Assert.Equal(7, store.GetSequence(TestFeeds.App, TestFeeds.Channel));
        Assert.Equal(2, store.GetSequence(TestFeeds.App, "beta"));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("{\"schema\": 2, \"sequences\": {}}")]
    [InlineData("{\"schema\": 1}")]
    public void Read_FailsClosedOnADamagedFile(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        File.WriteAllText(StatePath, content);
        var ex = Assert.Throws<InvalidDataException>(() => new FeedSequenceStore(StatePath).Accept(TestFeeds.Feed(1)));
        Assert.Contains("delete it to reset", ex.Message, StringComparison.Ordinal);
    }
}
