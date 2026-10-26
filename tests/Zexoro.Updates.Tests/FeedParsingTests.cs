using System.Text;

namespace Zexoro.Updates.Tests;

public sealed class FeedParsingTests
{
    private static UpdateFeed Parse(string json) => UpdateFeed.Parse(Encoding.UTF8.GetBytes(json));

    private static UpdateException Fails(string json) => Assert.Throws<UpdateException>(() => Parse(json));

    private static string Sample(string from, string to)
    {
        Assert.Contains(from, TestFeeds.SampleJson, StringComparison.Ordinal);
        return TestFeeds.SampleJson.Replace(from, to, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReadsTheContractExample()
    {
        var feed = Parse(TestFeeds.SampleJson);
        Assert.Equal("com.example.notes", feed.App);
        Assert.Equal("stable", feed.Channel);
        Assert.Equal(42, feed.Sequence);
        Assert.Equal(TestFeeds.Published, feed.Published);
        var release = Assert.Single(feed.Releases);
        Assert.Equal("1.2.3", release.Version.ToString());
        Assert.Equal("0.1.0", release.MinimumUpdater?.ToString());
        Assert.Equal("https://example.com/notes/1.2.3", release.Notes);
        var artifact = Assert.Single(release.Artifacts);
        Assert.Equal(TestFeeds.UserX64, artifact.Target);
        Assert.Equal(48213112, artifact.Size);
        Assert.Equal("9f2c000000000000000000000000000000000000000000000000000000000001", artifact.Sha256);
    }

    [Fact]
    public void Parse_IgnoresUnknownFields()
    {
        var feed = Parse(Sample("\"sequence\": 42,", "\"sequence\": 42, \"rollout\": { \"percent\": 10 }, \"future\": [1, 2],")
            .Replace("\"size\": 48213112,", "\"size\": 48213112, \"delta\": { \"from\": \"1.2.2\" },", StringComparison.Ordinal));
        Assert.Equal(42, feed.Sequence);
    }

    [Fact]
    public void Parse_RejectsNewerSchemaBeforeAnythingElse()
    {
        // A future schema may change everything else, so nothing else is looked at.
        var ex = Fails("""{ "releases": "something new", "schema": 2 }""");
        Assert.Equal(UpdateErrorKind.UpdaterTooOld, ex.Kind);
        Assert.Contains("update manually", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "app": "x" }""", "'schema' is missing")]
    [InlineData("""{ "schema": "1" }""", "positive integer")]
    [InlineData("""{ "schema": 0 }""", "positive integer")]
    [InlineData("""{ "schema": 1.5 }""", "positive integer")]
    [InlineData("""[]""", "JSON object")]
    [InlineData("""{ "schema": 1, """, "not valid")]
    [InlineData("""not json""", "not valid JSON")]
    public void Parse_RejectsBadSchema(string json, string message)
    {
        var ex = Fails(json);
        Assert.Equal(UpdateErrorKind.InvalidFeed, ex.Kind);
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"app\": \"com.example.notes\",", "", "'app' is missing")]
    [InlineData("\"sequence\": 42,", "", "'sequence' is missing")]
    [InlineData("\"sequence\": 42,", "\"sequence\": 0,", "sequence must be at least 1")]
    [InlineData("\"channel\": \"stable\",", "\"channel\": \"a/b\",", "not a valid channel")]
    [InlineData("\"published\": \"2026-10-26T12:00:00Z\",", "\"published\": \"yesterday\",", "ISO 8601")]
    [InlineData("\"version\": \"1.2.3\",", "\"version\": \"1.2\",", "releases[0]: 'version' '1.2'")]
    [InlineData("\"minimumUpdater\": \"0.1.0\",", "\"minimumUpdater\": \"v1\",", "minimumUpdater")]
    [InlineData("\"size\": 48213112,", "\"size\": 0,", "artifacts[0]: size must be a positive")]
    [InlineData("\"size\": 48213112,", "", "'size' is missing")]
    [InlineData("\"sha256\": \"9F2C", "\"sha256\": \"XX2C", "sha256 must be 64 hexadecimal digits")]
    [InlineData("\"os\": \"windows\",", "\"os\": \"\",", "'os' is missing")]
    [InlineData("\"arch\": \"x64\",", "\"arch\": \"x 64\",", "arch 'x 64' is not valid")]
    [InlineData("\"url\": \"https://example.com/dl/", "\"url\": \"/dl/", "not an absolute http or https URL")]
    public void Parse_RejectsInvalidValues(string from, string to, string message)
    {
        var ex = Fails(Sample(from, to));
        Assert.Equal(UpdateErrorKind.InvalidFeed, ex.Kind);
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsPlainHttpArtifacts()
    {
        var ex = Fails(Sample("https://example.com/dl/", "http://example.com/dl/"));
        Assert.Equal(UpdateErrorKind.InsecureUrl, ex.Kind);
    }

    [Fact]
    public void Parse_AcceptsHttpOnLocalhost()
    {
        var feed = Parse(Sample("https://example.com/dl/", "http://localhost:8080/dl/"));
        Assert.Equal("localhost", feed.Releases[0].Artifacts[0].Url.Host);
    }

    [Fact]
    public void Parse_RejectsDuplicateProperties()
    {
        var ex = Fails(Sample("\"sequence\": 42,", "\"sequence\": 42, \"sequence\": 43,"));
        Assert.Equal(UpdateErrorKind.InvalidFeed, ex.Kind);
    }

    [Fact]
    public void Parse_RejectsReleasesWithTheSamePrecedence()
    {
        var feed = TestFeeds.Feed(1, TestFeeds.Release("1.0.0")).ToJson();
        var json = Encoding.UTF8.GetString(feed);
        var release = json[json.IndexOf("    {", StringComparison.Ordinal)..json.LastIndexOf("  ]", StringComparison.Ordinal)].TrimEnd();
        var duplicated = json.Replace(release, release + ",\n" + release.Replace("1.0.0\"", "1.0.0+build.2\"", StringComparison.Ordinal), StringComparison.Ordinal);
        var ex = Fails(duplicated);
        Assert.Contains("same precedence", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsTwoArtifactsForOneTarget()
    {
        var ex = Assert.Throws<UpdateException>(() => TestFeeds.Release("1.0.0", null, TestFeeds.Artifact(file: "a.exe"), TestFeeds.Artifact(file: "b.exe")));
        Assert.Contains("more than one artifact", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToJson_RoundTripsAndSortsReleases()
    {
        var feed = TestFeeds.Feed(
            7,
            TestFeeds.Release("1.0.0"),
            TestFeeds.Release("2.0.0-beta.1", "0.2.0", TestFeeds.Artifact(), TestFeeds.Artifact(TestFeeds.MachineX64)),
            TestFeeds.Release("1.10.0"));
        var json = feed.ToJson();
        var text = Encoding.UTF8.GetString(json);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
        Assert.Contains("\"published\": \"2026-10-26T12:00:00Z\"", text, StringComparison.Ordinal);
        Assert.Contains("\"minimumUpdater\": \"0.2.0\"", text, StringComparison.Ordinal);

        var parsed = UpdateFeed.Parse(json);
        Assert.Equal(["2.0.0-beta.1", "1.10.0", "1.0.0"], parsed.Releases.Select(r => r.Version.ToString()));
        Assert.Equal(2, parsed.Releases[0].Artifacts.Count);
        Assert.Null(parsed.Releases[1].MinimumUpdater);
        Assert.Equal(json, parsed.ToJson());
    }
}
