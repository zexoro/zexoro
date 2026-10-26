using System.Text;
using System.Text.Json;

namespace Zexoro.Updater.Tests;

public sealed class CheckTests : IDisposable
{
    private readonly TestInstall _install = new();

    public void Dispose() => _install.Dispose();

    private JsonElement Json() => JsonDocument.Parse(_install.Stdout.ToString().Trim()).RootElement;

    [Fact]
    public void Check_ReportsAnUpdate()
    {
        _install.Publish("2.0.0");
        Assert.Equal(UpdaterExitCodes.Success, _install.Run("check", "--json"));
        var json = Json();
        Assert.Equal("available", json.GetProperty("status").GetString());
        Assert.Equal("1.0.0", json.GetProperty("installed").GetString());
        Assert.Equal("2.0.0", json.GetProperty("version").GetString());
        Assert.Equal("https://example.com/notes/2.0.0", json.GetProperty("notes").GetString());
        Assert.Equal("installer 2.0.0".Length, json.GetProperty("size").GetInt64());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("error").ValueKind);
        Assert.False(Directory.Exists(_install.State.Downloads));
    }

    [Fact]
    public void Check_WritesASentenceWithoutJson()
    {
        _install.Publish("2.0.0");
        Assert.Equal(UpdaterExitCodes.Success, _install.Run("check"));
        Assert.Contains("2.0.0 is available", _install.Stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Check_ReportsUpToDate()
    {
        _install.Publish("1.0.0");
        Assert.Equal(UpdaterExitCodes.UpToDate, _install.Run("check", "--json"));
        Assert.Equal("up-to-date", Json().GetProperty("status").GetString());
    }

    [Fact]
    public void Check_ReportsNetworkFailures()
    {
        _install.Handler.Offline = true;
        Assert.Equal(UpdaterExitCodes.Network, _install.Run("check", "--json"));
        Assert.Equal("error", Json().GetProperty("status").GetString());
        Assert.Contains("no route to host", Json().GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Check_RejectsATamperedFeed()
    {
        _install.Publish("2.0.0");
        var feed = _install.Handler.Responses[TestInstall.FeedUrl.AbsoluteUri];
        feed[^3] ^= 1;
        Assert.Equal(UpdaterExitCodes.VerificationFailed, _install.Run("check"));
    }

    [Fact]
    public void Check_RejectsAReplayedFeed()
    {
        _install.Publish("2.0.0", sequence: 5);
        Assert.Equal(UpdaterExitCodes.Success, _install.Run("check"));
        _install.Publish("2.0.0", sequence: 4);
        Assert.Equal(UpdaterExitCodes.VerificationFailed, _install.Run("check"));
        Assert.Contains("lower than the 5", _install.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Check_AsksForAManualUpdateWhenTheUpdaterIsTooOld()
    {
        _install.Publish("2.0.0", minimumUpdater: "999.0.0");
        Assert.Equal(UpdaterExitCodes.ManualUpdateNeeded, _install.Run("check", "--json"));
        Assert.Equal("2.0.0", Json().GetProperty("version").GetString());
    }

    [Fact]
    public void Check_NeedsUpdatesInTheInstallRecord()
    {
        File.WriteAllText(Path.Combine(_install.Root, "zexoro-install.json"), """
            {"schema": 1, "app": "com.example.notes", "version": "1.0.0", "os": "windows", "arch": "x64",
             "format": "nsis", "scope": "user", "strategy": "replace"}
            """);
        Assert.Equal(UpdaterExitCodes.ManualUpdateNeeded, _install.Run("check"));
        Assert.Contains("without [updates]", _install.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Download_StagesAVerifiedInstaller()
    {
        var installer = _install.Publish("2.0.0");
        Assert.Equal(UpdaterExitCodes.Success, _install.Run("download", "--json"));
        Assert.Equal("downloaded", Json().GetProperty("status").GetString());
        var staged = _install.State.ReadStaged();
        Assert.NotNull(staged);
        Assert.Equal("Notes-2.0.0-win-x64.exe", staged.FileName);
        Assert.Equal(installer, File.ReadAllBytes(staged.PathIn(_install.State.Downloads)));
    }

    [Fact]
    public void Download_ReusesAVerifiedDownload()
    {
        _install.Publish("2.0.0");
        Assert.Equal(UpdaterExitCodes.Success, _install.Run("download"));
        _install.Handler.Responses.Remove("https://example.com/dl/Notes-2.0.0-win-x64.exe");
        Assert.Equal(UpdaterExitCodes.Success, _install.Run("download"));
    }

    [Fact]
    public void Download_RejectsAnInstallerThatDoesNotMatchTheFeed()
    {
        _install.Publish("2.0.0");
        _install.Handler.Responses["https://example.com/dl/Notes-2.0.0-win-x64.exe"] = Encoding.UTF8.GetBytes("installer 2.0.X");
        Assert.Equal(UpdaterExitCodes.VerificationFailed, _install.Run("download"));
        Assert.Null(_install.State.ReadStaged());
    }

    [Fact]
    public void DownloadThenApply_InstallsTheUpdate()
    {
        _install.Publish("2.0.0");
        Assert.Equal(UpdaterExitCodes.Success, _install.Run("download"));
        _install.Processes.Installer = _ =>
        {
            _install.WriteStagedInstall("2.0.0");
            return 0;
        };

        Assert.Equal(UpdaterExitCodes.Success, _install.Run("apply"));
        Assert.Equal("2.0.0", _install.Registry.Entries[TestInstall.App].DisplayVersion);
    }

    [Theory]
    [InlineData("Notes-2.0.0-win-x64.exe", "Notes-2.0.0-win-x64.exe")]
    [InlineData("setup.msi", "com.example.notes-2.0.0.exe")]
    [InlineData("a%20b.exe", "com.example.notes-2.0.0.exe")]
    [InlineData("", "com.example.notes-2.0.0.exe")]
    public void FileNameFor_KeepsOnlyPlainExeNames(string file, string expected) =>
        Assert.Equal(expected, UpdateCommands.FileNameFor(TestInstall.App, Versioning.SemanticVersion.Parse("2.0.0"), new Uri("https://example.com/dl/" + file)));
}

public sealed class CommandLineTests : IDisposable
{
    private readonly TestInstall _install = new();

    public void Dispose() => _install.Dispose();

    [Theory]
    [InlineData]
    [InlineData("update")]
    [InlineData("check", "--restart")]
    [InlineData("download", "--wait-pid", "1")]
    [InlineData("apply", "--wait-pid")]
    [InlineData("apply", "--wait-pid", "-1")]
    [InlineData("apply", "--wait-pid", "x")]
    [InlineData("apply", "--from-root", "relative")]
    [InlineData("apply", "--bogus")]
    public void Run_RejectsBadArguments(params string[] args)
    {
        Assert.Equal(UpdaterExitCodes.UsageError, _install.Run(args));
        Assert.Contains("--help", _install.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_PrintsHelpAndVersion()
    {
        Assert.Equal(0, _install.Run("--help"));
        Assert.Contains("zexoro-updater check", _install.Stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, _install.Run("--version"));
        Assert.Contains(ProductInfo.Version, _install.Stdout.ToString(), StringComparison.Ordinal);
    }
}
