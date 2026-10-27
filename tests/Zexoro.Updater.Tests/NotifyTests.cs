using System.Text.Json;

namespace Zexoro.Updater.Tests;

/// <summary>The notify strategy: deb installs are only checked; the package manager installs updates.</summary>
public sealed class NotifyTests : IDisposable
{
    private readonly TestInstall _install = new(scope: "machine", format: "deb");

    public void Dispose() => _install.Dispose();

    private JsonElement Json() => JsonDocument.Parse(_install.Stdout.ToString().Trim()).RootElement;

    [Fact]
    public void Check_ReportsTheUpdateAndItsUrl()
    {
        _install.Publish("2.0.0");
        Assert.Equal(UpdaterExitCodes.Success, _install.Run("check", "--json"));
        var json = Json();
        Assert.Equal("available", json.GetProperty("status").GetString());
        Assert.Equal("1.0.0", json.GetProperty("installed").GetString());
        Assert.Equal("2.0.0", json.GetProperty("version").GetString());
        Assert.Equal("https://example.com/dl/Notes-2.0.0-win-x64.deb", json.GetProperty("url").GetString());
        Assert.False(Directory.Exists(_install.State.Downloads));
    }

    [Fact]
    public void Check_ReportsUpToDateWithoutUrl()
    {
        _install.Publish("1.0.0");
        Assert.Equal(UpdaterExitCodes.UpToDate, _install.Run("check", "--json"));
        Assert.Equal(JsonValueKind.Null, Json().GetProperty("url").ValueKind);
    }

    [Fact]
    public void Check_RejectsATamperedFeed()
    {
        _install.Publish("2.0.0");
        _install.Handler.Responses[TestInstall.FeedUrl.AbsoluteUri][^3] ^= 1;
        Assert.Equal(UpdaterExitCodes.VerificationFailed, _install.Run("check"));
    }

    [Theory]
    [InlineData("download")]
    [InlineData("apply")]
    public void DownloadAndApply_ExitSevenAndTouchNothing(string command)
    {
        _install.Publish("2.0.0");
        var before = _install.Snapshot();
        _install.Handler.Offline = true;

        Assert.Equal(UpdaterExitCodes.ManualUpdateNeeded, _install.Run(command, "--json"));

        var json = Json();
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Equal("1.0.0", json.GetProperty("installed").GetString());
        Assert.Contains("apt or dpkg", json.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, _install.Snapshot());
        Assert.False(Directory.Exists(Path.Combine(_install.LocalAppData, "zexoro")));
        Assert.Empty(_install.Processes.Log);
    }

    [Fact]
    public void Apply_IgnoresAStagedDownload()
    {
        _install.Stage("2.0.0");
        var before = _install.Snapshot();
        Assert.Equal(UpdaterExitCodes.ManualUpdateNeeded, _install.Run("apply", "--wait-pid", "77", "--restart"));
        Assert.Equal(before, _install.Snapshot());
        Assert.False(File.Exists(_install.State.LastApplyFile));
    }

    [Theory]
    [InlineData("deb", "apt or dpkg")]
    [InlineData("rpm", "dnf, zypper or rpm")]
    [InlineData("apk", "apk")]
    [InlineData("other", "the system's package manager")]
    public void PackageManagerFor_NamesTheTool(string format, string expected) =>
        Assert.Equal(expected, Notify.PackageManagerFor(format));
}
