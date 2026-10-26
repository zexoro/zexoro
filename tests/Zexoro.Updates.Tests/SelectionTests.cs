namespace Zexoro.Updates.Tests;

public sealed class SelectionTests
{
    private static readonly Zexoro.Versioning.SemanticVersion Updater = TestFeeds.V("0.5.0");

    private static UpdateSelection Select(string installed, params FeedRelease[] releases) =>
        ReleaseSelector.Select(TestFeeds.Feed(1, releases), TestFeeds.V(installed), TestFeeds.UserX64, Updater);

    [Fact]
    public void Select_PicksTheHighestNewerRelease()
    {
        var selection = Select("1.0.0", TestFeeds.Release("1.0.1"), TestFeeds.Release("1.2.0"), TestFeeds.Release("1.1.0"));
        Assert.Equal(UpdateStatus.Available, selection.Status);
        Assert.Equal("1.2.0", selection.Release?.Version.ToString());
        Assert.Equal("app-1.2.0.exe", selection.Artifact?.Url.Segments[^1]);
    }

    [Fact]
    public void Select_ComparesNumericallyNotLexically()
    {
        Assert.Equal("1.10.0", Select("1.9.0", TestFeeds.Release("1.10.0"), TestFeeds.Release("1.9.1")).Release?.Version.ToString());
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0", "0.9.0")]
    [InlineData("1.0.0+build.1", "1.0.0+build.2")]
    [InlineData("1.0.0", "1.0.0-rc.1")]
    public void Select_NeverOffersSameOrLower(string installed, string feed)
    {
        var selection = Select(installed, TestFeeds.Release(feed));
        Assert.Equal(UpdateStatus.UpToDate, selection.Status);
        Assert.Null(selection.Release);
    }

    [Theory]
    [InlineData("1.0.0-beta.2", "1.0.0", "1.0.0")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", "1.0.0-beta.10")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.1")]
    [InlineData("1.0.0", "1.1.0-rc.1", "1.1.0-rc.1")]
    public void Select_TreatsPrereleasesAsOrdinaryVersions(string installed, string feed, string expected)
    {
        Assert.Equal(expected, Select(installed, TestFeeds.Release(feed)).Release?.Version.ToString());
    }

    [Fact]
    public void Select_SkipsReleasesWithoutAnArtifactForTheTarget()
    {
        var selection = Select(
            "1.0.0",
            TestFeeds.Release("2.0.0", null, TestFeeds.Artifact(TestFeeds.MachineX64)),
            TestFeeds.Release("1.5.0", null, TestFeeds.Artifact(TestFeeds.UserX64 with { Arch = "arm64" })),
            TestFeeds.Release("1.1.0"));
        Assert.Equal("1.1.0", selection.Release?.Version.ToString());
        Assert.Equal(TestFeeds.UserX64, selection.Artifact?.Target);
    }

    [Fact]
    public void Select_IsUpToDateWithoutMatchingArtifacts()
    {
        Assert.Equal(UpdateStatus.UpToDate, Select("1.0.0", TestFeeds.Release("2.0.0", null, TestFeeds.Artifact(TestFeeds.MachineX64))).Status);
        Assert.Equal(UpdateStatus.UpToDate, Select("1.0.0").Status);
    }

    [Fact]
    public void Select_FallsBackBelowAReleaseThatNeedsANewerUpdater()
    {
        var selection = Select("1.0.0", TestFeeds.Release("3.0.0", "0.6.0"), TestFeeds.Release("2.0.0", "0.5.0"));
        Assert.Equal(UpdateStatus.Available, selection.Status);
        Assert.Equal("2.0.0", selection.Release?.Version.ToString());
        Assert.Equal("3.0.0", selection.Newest?.Version.ToString());
    }

    [Fact]
    public void Select_ReportsUpdaterTooOld()
    {
        var selection = Select("1.0.0", TestFeeds.Release("3.0.0", "1.0.0"));
        Assert.Equal(UpdateStatus.UpdaterTooOld, selection.Status);
        Assert.Equal("3.0.0", selection.Release?.Version.ToString());
    }

    [Fact]
    public void Select_PrereleaseUpdaterIsOlderThanItsRelease()
    {
        var selection = ReleaseSelector.Select(
            TestFeeds.Feed(1, TestFeeds.Release("2.0.0", "0.5.0")), TestFeeds.V("1.0.0"), TestFeeds.UserX64, TestFeeds.V("0.5.0-dev"));
        Assert.Equal(UpdateStatus.UpdaterTooOld, selection.Status);
    }
}
