using System.Text;

namespace Zexoro.Updater.Tests;

/// <summary>
/// macOS bundles: the updater in <c>Contents/MacOS</c> finds the record in <c>Contents/Resources</c>;
/// check and download work, apply exits 7 until the bundle strategy is implemented.
/// </summary>
public sealed class BundleTests : IDisposable
{
    private readonly TestInstall _install = new(format: "app");
    private readonly string _bundle;
    private readonly string _updater;

    public BundleTests()
    {
        _bundle = Path.Combine(_install.Root, "Notes.app");
        Directory.CreateDirectory(Path.Combine(_bundle, "Contents", "MacOS"));
        Directory.CreateDirectory(Path.Combine(_bundle, "Contents", "Resources"));
        _updater = Path.Combine(_bundle, "Contents", "MacOS", "zexoro-updater");
        File.WriteAllText(_updater, "updater 1.0.0");
        var record = Encoding.UTF8.GetString(_install.Record("1.0.0", "Contents/MacOS/Notes"))
            .Replace("\"strategy\": \"replace\"", "\"strategy\": \"bundle\"", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_bundle, "Contents", "Resources", "zexoro-install.json"), record);
    }

    public void Dispose() => _install.Dispose();

    private int Run(params string[] args) => UpdaterApp.Run(args, _install.Stdout, _install.Stderr, _install.Environment(_updater));

    [Theory]
    [InlineData("/Apps/Notes.app/Contents/MacOS", "/Apps/Notes.app/Contents/Resources/zexoro-install.json")]
    [InlineData("/Apps/Notes.APP/Contents/MacOS/", "/Apps/Notes.APP/Contents/Resources/zexoro-install.json")]
    [InlineData("/opt/notes", "/opt/notes/zexoro-install.json")]
    [InlineData("/opt/Contents/MacOS", "/opt/Contents/MacOS/zexoro-install.json")]
    [InlineData("/Apps/Notes.app/Contents/Resources", "/Apps/Notes.app/Contents/Resources/zexoro-install.json")]
    public void RecordPath_LooksInResourcesInsideABundle(string directory, string expected)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix paths.");
        Assert.Equal(expected, InstallLayout.RecordPath(directory));
    }

    [Fact]
    public void CheckAndDownload_ReadTheRecordInResources()
    {
        _install.Publish("2.0.0");

        Assert.Equal(UpdaterExitCodes.Success, Run("check"));
        Assert.Equal(UpdaterExitCodes.Success, Run("download"));

        Assert.True(File.Exists(Path.Combine(_install.State.Downloads, "Notes-2.0.0-win-x64.zip")));
    }

    [Fact]
    public void Apply_ExitsSevenAndTouchesNothing()
    {
        _install.Stage("2.0.0");
        var before = _install.Snapshot();

        Assert.Equal(UpdaterExitCodes.ManualUpdateNeeded, Run("apply", "--wait-pid", "77", "--restart"));

        Assert.Contains("cannot apply macOS bundle updates yet", _install.Stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, _install.Snapshot());
        Assert.Empty(_install.Processes.Log);
    }
}
