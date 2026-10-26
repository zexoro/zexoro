namespace Zexoro.Updates.Tests;

public sealed class InstallRecordTests : IDisposable
{
    private readonly UpdateSigningKey _key = UpdateSigningKey.Create();

    public void Dispose() => _key.Dispose();

    private static string Json(string extra = "", string schema = "1") => $$"""
        {
          "schema": {{schema}}, "app": "com.example.notes", "version": "1.2.3-beta.1",
          "os": "windows", "arch": "x64", "format": "nsis", "scope": "user",
          "strategy": "replace", "unknown": [1, 2]{{extra}}
        }
        """;

    private string UpdatesJson(string main = "bin/Notes.exe", string feed = "https://example.com/stable/feed.json") =>
        Json($$""", "channel": "stable", "feed": "{{feed}}", "keys": ["{{_key.PublicKey.Text}}"], "main": "{{main}}" """);

    [Fact]
    public void Parse_ReadsAnInstallWithUpdates()
    {
        var record = UpdateInstallRecord.Parse(TestFeeds.Utf8(UpdatesJson()));
        Assert.Equal("com.example.notes", record.App);
        Assert.Equal(TestFeeds.V("1.2.3-beta.1"), record.Version);
        Assert.Equal(TestFeeds.UserX64, record.Target);
        Assert.Equal("replace", record.Strategy);
        Assert.Equal("bin/Notes.exe", record.Main);
        var installed = record.ToInstalledApp();
        Assert.Equal("stable", installed.Channel);
        Assert.Equal(new Uri("https://example.com/stable/feed.json"), installed.Feed);
        Assert.Equal(_key.PublicKey, Assert.Single(installed.Keys));
    }

    [Fact]
    public void Parse_ReadsAnInstallWithoutUpdates()
    {
        var record = UpdateInstallRecord.Parse(TestFeeds.Utf8(Json()));
        Assert.Null(record.Channel);
        Assert.Empty(record.Keys);
        var ex = Assert.Throws<UpdateException>(record.ToInstalledApp);
        Assert.Equal(UpdateErrorKind.InvalidInstallRecord, ex.Kind);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"schema": 1}""")]
    [InlineData("""{"schema": "1", "app": "a"}""")]
    public void Parse_RejectsMalformedRecords(string json)
    {
        var ex = Assert.Throws<UpdateException>(() => UpdateInstallRecord.Parse(TestFeeds.Utf8(json)));
        Assert.Equal(UpdateErrorKind.InvalidInstallRecord, ex.Kind);
    }

    [Fact]
    public void Parse_RejectsANewerSchema()
    {
        var ex = Assert.Throws<UpdateException>(() => UpdateInstallRecord.Parse(TestFeeds.Utf8(Json(schema: "2"))));
        Assert.Contains("update manually", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../Notes.exe")]
    [InlineData("C:/Notes.exe")]
    [InlineData("/Notes.exe")]
    [InlineData("bin//Notes.exe")]
    public void Parse_RejectsAMainOutsideTheAppDirectory(string main) =>
        Assert.Throws<UpdateException>(() => UpdateInstallRecord.Parse(TestFeeds.Utf8(UpdatesJson(main: main))));

    [Fact]
    public void Parse_RejectsAnInsecureFeed() =>
        Assert.Throws<UpdateException>(() => UpdateInstallRecord.Parse(TestFeeds.Utf8(UpdatesJson(feed: "http://example.com/feed.json"))));

    [Fact]
    public void Parse_RejectsAnInvalidKey()
    {
        var json = Json(""", "channel": "stable", "feed": "https://example.com/f.json", "keys": ["AAAA"]""");
        Assert.Throws<UpdateException>(() => UpdateInstallRecord.Parse(TestFeeds.Utf8(json)));
    }

    [Fact]
    public void Load_ReportsAMissingFile()
    {
        var ex = Assert.Throws<UpdateException>(() => UpdateInstallRecord.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "zexoro-install.json")));
        Assert.Equal(UpdateErrorKind.InvalidInstallRecord, ex.Kind);
    }
}
