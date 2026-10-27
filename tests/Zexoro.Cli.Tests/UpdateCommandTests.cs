using System.Text;
using Zexoro.Updates;
using Zexoro.Versioning;

namespace Zexoro.Cli.Tests;

public sealed class UpdateCommandTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-cli-updates-");
    private readonly Dictionary<string, string> _variables = [];
    private StringWriter _stdout = new();
    private StringWriter _stderr = new();

    public void Dispose()
    {
        _stdout.Dispose();
        _stderr.Dispose();
        _dir.Delete(recursive: true);
    }

    private string PathOf(string relative) => Path.Combine(_dir.FullName, relative);

    private int Run(params string[] args)
    {
        _stdout = new StringWriter();
        _stderr = new StringWriter();
        var environment = new CliEnvironment(_dir.FullName, name => _variables.GetValueOrDefault(name)) { Time = new FixedTime(Now) };
        return App.Run(args, _stdout, _stderr, environment);
    }

    private void WriteFragment(string file, string version, string arch = "x64", char hash = 'a', string channel = "stable")
    {
        var artifact = new FeedArtifact(
            new UpdateTarget("windows", arch, "nsis", "user"), new Uri($"https://example.com/dl/{version}-{arch}.exe"), 10, new string(hash, 64));
        var fragment = new ReleaseFragment("com.example.notes", channel, new FeedRelease(SemanticVersion.Parse(version), [artifact]));
        File.WriteAllBytes(PathOf(file), fragment.ToJson());
    }

    private UpdateFeed VerifiedFeed()
    {
        var key = UpdatePublicKey.Parse(File.ReadAllText(PathOf("zexoro-update.pub")));
        return FeedVerifier.Verify(File.ReadAllBytes(PathOf("feed.json")), File.ReadAllBytes(PathOf("feed.json.sig")), [key]);
    }

    [Fact]
    public void KeysNew_WritesAKeyPair()
    {
        Assert.Equal(0, Run("keys", "new"));
        var pem = File.ReadAllText(PathOf("zexoro-update.key.pem"));
        Assert.Contains("-----BEGIN PRIVATE KEY-----", pem, StringComparison.Ordinal);
        var publicText = File.ReadAllText(PathOf("zexoro-update.pub"));
        using var key = UpdateSigningKey.FromPem(pem);
        Assert.Equal(key.PublicKey.Text + "\n", publicText);
        Assert.Contains(key.PublicKey.Text, _stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("can\nno longer be updated automatically", _stdout.ToString().ReplaceLineEndings("\n"), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(PathOf("zexoro-update.key.pem")));
        }
    }

    [Fact]
    public void KeysNew_EncryptsWithThePasswordVariable()
    {
        _variables["ZEXORO_UPDATE_KEY_PASSWORD"] = "secret";
        Assert.Equal(0, Run("keys", "new", "--output", "keys", "--name", "release"));
        var pem = File.ReadAllText(PathOf("keys/release.key.pem"));
        Assert.Contains("ENCRYPTED PRIVATE KEY", pem, StringComparison.Ordinal);
        using var key = UpdateSigningKey.FromPem(pem, "secret");
        Assert.True(File.Exists(PathOf("keys/release.pub")));
        Assert.Contains("(encrypted)", _stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void KeysNew_NeverOverwrites()
    {
        Assert.Equal(0, Run("keys", "new"));
        var before = File.ReadAllText(PathOf("zexoro-update.key.pem"));
        Assert.Equal(1, Run("keys", "new"));
        Assert.Contains("never overwrites", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(PathOf("zexoro-update.key.pem")));
    }

    [Theory]
    [InlineData("keys")]
    [InlineData("keys", "old")]
    [InlineData("keys", "new", "--name", "../x")]
    [InlineData("keys", "new", "--bogus", "x")]
    [InlineData("feed")]
    [InlineData("feed", "add", "x.json")]
    [InlineData("feed", "add", "--feed", "feed.json")]
    [InlineData("feed", "verify", "feed.json")]
    public void Commands_RejectBadUsage(params string[] args)
    {
        Assert.Equal(2, Run(args));
    }

    [Fact]
    public void FeedAdd_CreatesSignsAndExtendsAFeed()
    {
        Assert.Equal(0, Run("keys", "new"));
        WriteFragment("x64.release.json", "1.0.0");
        WriteFragment("arm64.release.json", "1.0.0", "arm64");
        Assert.Equal(0, Run("feed", "add", "--feed", "feed.json", "--key", "zexoro-update.key.pem", "x64.release.json", "arm64.release.json"));
        var feed = VerifiedFeed();
        Assert.Equal(1, feed.Sequence);
        Assert.Equal(Now, feed.Published);
        Assert.Equal(2, Assert.Single(feed.Releases).Artifacts.Count);

        WriteFragment("next.release.json", "1.1.0");
        Assert.Equal(0, Run("feed", "add", "--feed=feed.json", "--key=zexoro-update.key.pem", "next.release.json"));
        feed = VerifiedFeed();
        Assert.Equal(2, feed.Sequence);
        Assert.Equal(["1.1.0", "1.0.0"], feed.Releases.Select(r => r.Version.ToString()));

        Assert.Equal(0, Run("feed", "verify", "--public-key", "zexoro-update.pub", "feed.json"));
        Assert.Contains("signature valid", _stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("sequence 2", _stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FeedAdd_ReadsTheKeyFromTheEnvironment()
    {
        Assert.Equal(0, Run("keys", "new"));
        _variables["ZEXORO_UPDATE_KEY"] = File.ReadAllText(PathOf("zexoro-update.key.pem"));
        WriteFragment("a.release.json", "1.0.0");
        Assert.Equal(0, Run("feed", "add", "--feed", "out/feed.json", "a.release.json"));
        Assert.True(File.Exists(PathOf("out/feed.json.sig")));
    }

    [Fact]
    public void FeedAdd_NeedsAKey()
    {
        WriteFragment("a.release.json", "1.0.0");
        Assert.Equal(1, Run("feed", "add", "--feed", "feed.json", "a.release.json"));
        Assert.Contains("ZEXORO_UPDATE_KEY", _stderr.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(PathOf("feed.json")));
    }

    [Fact]
    public void FeedAdd_LeavesAnUnchangedFeedAlone()
    {
        Assert.Equal(0, Run("keys", "new"));
        WriteFragment("a.release.json", "1.0.0");
        Assert.Equal(0, Run("feed", "add", "--feed", "feed.json", "--key", "zexoro-update.key.pem", "a.release.json"));
        var before = File.ReadAllBytes(PathOf("feed.json"));
        Assert.Equal(0, Run("feed", "add", "--feed", "feed.json", "--key", "zexoro-update.key.pem", "a.release.json"));
        Assert.Contains("nothing changed", _stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(PathOf("feed.json")));
    }

    [Fact]
    public void FeedAdd_RejectsConflictsWithoutWriting()
    {
        Assert.Equal(0, Run("keys", "new"));
        WriteFragment("a.release.json", "1.0.0");
        Assert.Equal(0, Run("feed", "add", "--feed", "feed.json", "--key", "zexoro-update.key.pem", "a.release.json"));
        var before = File.ReadAllBytes(PathOf("feed.json"));

        WriteFragment("changed.release.json", "1.0.0", hash: 'b');
        Assert.Equal(1, Run("feed", "add", "--feed", "feed.json", "--key", "zexoro-update.key.pem", "changed.release.json"));
        Assert.Contains("publish a new version", _stderr.ToString(), StringComparison.Ordinal);

        WriteFragment("beta.release.json", "2.0.0", channel: "beta");
        Assert.Equal(1, Run("feed", "add", "--feed", "feed.json", "--key", "zexoro-update.key.pem", "beta.release.json"));
        Assert.Contains("channel 'beta'", _stderr.ToString(), StringComparison.Ordinal);

        File.WriteAllText(PathOf("broken.release.json"), "{}");
        Assert.Equal(1, Run("feed", "add", "--feed", "feed.json", "--key", "zexoro-update.key.pem", "broken.release.json"));
        Assert.Contains("broken.release.json", _stderr.ToString(), StringComparison.Ordinal);

        Assert.Equal(before, File.ReadAllBytes(PathOf("feed.json")));
    }

    [Fact]
    public void FeedAdd_RejectsAWrongKeyPassword()
    {
        _variables["ZEXORO_UPDATE_KEY_PASSWORD"] = "right";
        Assert.Equal(0, Run("keys", "new"));
        _variables["ZEXORO_UPDATE_KEY_PASSWORD"] = "wrong";
        WriteFragment("a.release.json", "1.0.0");
        Assert.Equal(1, Run("feed", "add", "--feed", "feed.json", "--key", "zexoro-update.key.pem", "a.release.json"));
        Assert.Contains("wrong password", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FeedVerify_RejectsATamperedFeedAndAnotherKey()
    {
        Assert.Equal(0, Run("keys", "new"));
        WriteFragment("a.release.json", "1.0.0");
        Assert.Equal(0, Run("feed", "add", "--feed", "feed.json", "--key", "zexoro-update.key.pem", "a.release.json"));

        Assert.Equal(0, Run("keys", "new", "--name", "other"));
        Assert.Equal(1, Run("feed", "verify", "--public-key", "other.pub", "feed.json"));
        Assert.Contains("does not match any trusted key", _stderr.ToString(), StringComparison.Ordinal);

        var publicKey = File.ReadAllText(PathOf("zexoro-update.pub")).Trim();
        File.WriteAllText(PathOf("feed.json"), File.ReadAllText(PathOf("feed.json")).Replace("1.0.0", "9.0.0", StringComparison.Ordinal));
        Assert.Equal(1, Run("feed", "verify", "--public-key", publicKey, "feed.json"));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
