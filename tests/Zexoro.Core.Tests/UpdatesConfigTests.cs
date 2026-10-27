using System.Security.Cryptography;
using Zexoro.Configuration;
using Zexoro.Toml;

namespace Zexoro.Core.Tests;

public sealed class UpdatesConfigTests : IDisposable
{
    private const string Minimal = """
        [app]
        id = "com.example.notes"
        name = "Example Notes"
        version = "1.2.3"
        publisher = "Example Ltd."
        icon = "assets/app.ico"

        [input]
        dir = "build/win-x64"
        main = "ExampleNotes.exe"

        """;

    private static readonly string Key = NewKey();

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-updates-config-");

    public void Dispose() => _dir.Delete(recursive: true);

    private static string NewKey()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    }

    private ZexoroConfig Parse(string updates, ConfigOverrides? overrides = null) =>
        ZexoroConfig.Parse(Minimal + updates, _dir.FullName, "zexoro.toml", overrides);

    private IReadOnlyList<ConfigError> Errors(string updates, ConfigOverrides? overrides = null) =>
        Assert.Throws<ConfigurationException>(() => Parse(updates, overrides)).Errors;

    private static string Table(string feed = "https://example.com/updates/{channel}/feed.json", string download = "https://example.com/dl/{channel}/{file}", string? keys = null, string extra = "") => $"""
        [updates]
        feed = "{feed}"
        download = "{download}"
        keys = {keys ?? $"[\"{Key}\"]"}
        {extra}
        """;

    [Fact]
    public void Parse_WithoutUpdates_HasNone() =>
        Assert.Null(ZexoroConfig.Parse(Minimal, _dir.FullName).Updates);

    [Fact]
    public void Parse_Updates_ReadsEveryValueAndDefaultsToStable()
    {
        var updates = Parse(Table()).Updates!;

        Assert.Equal("stable", updates.Channel);
        Assert.Equal("https://example.com/updates/{channel}/feed.json", updates.FeedTemplate);
        Assert.Equal(new Uri("https://example.com/updates/stable/feed.json"), updates.FeedUrl);
        Assert.Equal(new Uri("https://example.com/dl/stable/Notes-1.0.0-win-x64.exe"), updates.DownloadUrl("Notes-1.0.0-win-x64.exe", "1.0.0"));
        Assert.Equal([Key], updates.ResolveKeys());
    }

    [Fact]
    public void Parse_ChannelOverride_ReplacesTheConfiguredChannel()
    {
        var updates = Parse(Table(extra: "channel = \"beta\""), new ConfigOverrides { Channel = "nightly" }).Updates!;

        Assert.Equal("nightly", updates.Channel);
        Assert.Equal(new Uri("https://example.com/updates/nightly/feed.json"), updates.FeedUrl);
    }

    [Fact]
    public void DownloadUrl_EscapesValues()
    {
        var updates = Parse(Table(download: "https://example.com/{version}/{file}")).Updates!;

        Assert.Equal("https://example.com/1.0.0-beta.1%2Bbuild.7/Notes.exe", updates.DownloadUrl("Notes.exe", "1.0.0-beta.1+build.7").AbsoluteUri);
    }

    [Fact]
    public void Parse_HttpOnLocalhost_IsAllowed()
    {
        var updates = Parse(Table(feed: "http://localhost:8080/{channel}/feed.json", download: "http://127.0.0.1:8080/{file}")).Updates!;

        Assert.Equal(new Uri("http://localhost:8080/stable/feed.json"), updates.FeedUrl);
    }

    [Theory]
    [InlineData("https://example.com/feed.json", "must contain {channel}")]
    [InlineData("http://example.com/{channel}/feed.json", "is not an https URL")]
    [InlineData("https://example.com/{channel}/{version}.json", "unknown placeholder")]
    [InlineData("{channel}/feed.json", "is not an https URL")]
    public void Parse_InvalidFeed_ReportsPosition(string feed, string message)
    {
        var error = Assert.Single(Errors(Table(feed: feed)));

        Assert.Contains("updates.feed", error.Message, StringComparison.Ordinal);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.Equal(new TomlPosition(12, 8), error.Position);
    }

    [Theory]
    [InlineData("ftp://example.com/{file}", "is not an https URL")]
    [InlineData("https://example.com/{name}", "unknown placeholder")]
    public void Parse_InvalidDownload_ReportsPosition(string download, string message)
    {
        var error = Assert.Single(Errors(Table(download: download)));

        Assert.Contains("updates.download", error.Message, StringComparison.Ordinal);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.Equal(13, error.Position?.Line);
    }

    [Fact]
    public void Parse_InvalidChannel_ReportsPosition()
    {
        var error = Assert.Single(Errors(Table(extra: "channel = \"be ta\"")));

        Assert.Contains("updates.channel: 'be ta' is not a valid channel", error.Message, StringComparison.Ordinal);
        Assert.Equal(15, error.Position?.Line);
    }

    [Fact]
    public void Parse_InvalidChannelOverride_NamesTheOption()
    {
        var error = Assert.Single(Errors(Table(), new ConfigOverrides { Channel = "../x" }));

        Assert.Equal("--channel", error.Source);
    }

    [Fact]
    public void Parse_ChannelOverrideWithoutUpdates_IsAnError()
    {
        var error = Assert.Single(Assert.Throws<ConfigurationException>(
            () => ZexoroConfig.Parse(Minimal, _dir.FullName, "zexoro.toml", new ConfigOverrides { Channel = "beta" })).Errors);

        Assert.Equal("--channel", error.Source);
        Assert.Contains("[updates]", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]", "at least one public key")]
    [InlineData("[\"bm90IGEga2V5\"]", "updates.keys[0]: not a valid update public key")]
    [InlineData("[\"%%%\"]", "expected base64 text")]
    [InlineData("[42]", "updates.keys[0] must be a base64 public key or { file")]
    [InlineData("[{ path = \"k.pub\" }]", "unknown key 'updates.keys[0].path'")]
    [InlineData("\"key\"", "updates.keys must be an array")]
    public void Parse_InvalidKeys_ReportsPosition(string keys, string message)
    {
        var errors = Errors(Table(keys: keys));

        Assert.Contains(errors, e => e.Message.Contains(message, StringComparison.Ordinal) && e.Position?.Line == 14);
    }

    [Fact]
    public void Parse_MissingKeys_IsAnError()
    {
        var error = Assert.Single(Errors("[updates]\nfeed = \"https://e.com/{channel}\"\ndownload = \"https://e.com/{file}\"\n"));

        Assert.Contains("[updates]: missing required key 'keys'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnknownKey_SuggestsTheClosest()
    {
        var error = Assert.Single(Errors(Table(extra: "chanel = \"beta\"")));

        Assert.Contains("unknown key 'updates.chanel'; did you mean 'channel'?", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RsaKey_IsRejected()
    {
        using var rsa = RSA.Create(2048);
        var error = Assert.Single(Errors(Table(keys: $"[\"{Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())}\"]")));

        Assert.Contains("not an elliptic curve key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveKeys_ReadsKeyFilesAndDropsDuplicates()
    {
        var other = NewKey();
        Directory.CreateDirectory(Path.Combine(_dir.FullName, "keys"));
        File.WriteAllText(Path.Combine(_dir.FullName, "keys", "update.pub"), other + "\n");
        File.WriteAllText(Path.Combine(_dir.FullName, "keys", "same.pub"), Key);

        var updates = Parse(Table(keys: $"[\"{Key}\", {{ file = \"keys/update.pub\" }}, {{ file = \"keys/same.pub\" }}]")).Updates!;

        Assert.Equal([Key, other], updates.ResolveKeys());
    }

    [Fact]
    public void ResolveKeys_MissingOrInvalidFile_ReportsPosition()
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "bad.pub"), "garbage");
        var updates = Parse(Table(keys: "[{ file = \"missing.pub\" }, { file = \"bad.pub\" }]")).Updates!;

        var errors = Assert.Throws<ConfigurationException>(updates.ResolveKeys).Errors;

        Assert.Equal(2, errors.Count);
        Assert.Contains("updates.keys[0]: cannot read the key file", errors[0].Message, StringComparison.Ordinal);
        Assert.Equal(new TomlPosition(14, 9), errors[0].Position);
        Assert.Contains("updates.keys[1]", errors[1].Message, StringComparison.Ordinal);
        Assert.Contains("holds no valid update public key", errors[1].Message, StringComparison.Ordinal);
    }
}
