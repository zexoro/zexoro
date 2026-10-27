using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Zexoro.Updater.Tests;

/// <summary>
/// The Unix platform layer (docs/design/0006-desktop-formats.md section 2): state directories, the
/// private temporary copy, and waiting for a process that is not a child. The tests that need a
/// real Unix system are skipped on Windows.
/// </summary>
public sealed class UnixTests : IDisposable
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly TestInstall _install = new(format: "tar-gz");

    public void Dispose() => _install.Dispose();

    private static Func<string, string?> Variables(string? xdgStateHome) => name => name == "XDG_STATE_HOME" ? xdgStateHome : null;

    [Fact]
    public void StateBaseFor_FollowsEachSystemsConvention()
    {
        Assert.Equal(@"C:\Users\u\AppData\Local", UpdaterEnvironment.StateBaseFor(HostSystem.Windows, Variables("/x"), @"C:\Users\u", @"C:\Users\u\AppData\Local"));
        Assert.Equal(Path.Combine("/Users/u", "Library", "Application Support"), UpdaterEnvironment.StateBaseFor(HostSystem.MacOS, Variables("/x"), "/Users/u", ""));
        Assert.Equal("/var/state/u", UpdaterEnvironment.StateBaseFor(HostSystem.Linux, Variables("/var/state/u"), "/home/u", ""));
        Assert.Equal(Path.Combine("/home/u", ".local", "state"), UpdaterEnvironment.StateBaseFor(HostSystem.Linux, Variables(null), "/home/u", ""));
        Assert.Equal(Path.Combine("/home/u", ".local", "state"), UpdaterEnvironment.StateBaseFor(HostSystem.Linux, Variables(""), "/home/u", ""));
    }

    [Fact]
    public void StateBaseFor_IgnoresARelativeXdgStateHome()
    {
        // The XDG Base Directory specification: relative paths are invalid and must be ignored.
        Assert.Equal(Path.Combine("/home/u", ".local", "state"), UpdaterEnvironment.StateBaseFor(HostSystem.Linux, Variables("state"), "/home/u", ""));
    }

    [Fact]
    public void State_LivesUnderZexoroUpdaterAndTheAppId()
    {
        var state = new UpdaterState(Path.Combine("/home/u", ".local", "state"), "com.example.notes");
        Assert.Equal(Path.Combine("/home/u", ".local", "state", "zexoro", "updater", "com.example.notes"), state.Directory);
    }

    [Fact]
    public void Apply_RunsAPrivateCopyNamedLikeTheUpdater()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes");
        }

        _install.Stage("2.0.0", TarGzApplyTests.Archive(_install, "2.0.0"));

        Assert.Equal(UpdaterExitCodes.Success, _install.Run("apply"));

        Assert.StartsWith("run zexoro-updater apply --from-root ", _install.Processes.Log[0], StringComparison.Ordinal);
        var (directory, file) = Assert.Single(_install.Processes.CopyModes);
        Assert.Equal(Private, directory);
        Assert.Equal(Private, file);
        Assert.Empty(Directory.EnumerateDirectories(_install.Temp, "zexoro-updater-*"));
    }

    [Fact]
    public void Apply_RemovesOnlyOldCopiesOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows removes every copy that is not running");
        }

        var old = Directory.CreateDirectory(Path.Combine(_install.Temp, "zexoro-updater-old")).FullName;
        File.WriteAllText(Path.Combine(old, "zexoro-updater"), "old");
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow - ApplyCommand.UnixCopyMaxAge - TimeSpan.FromMinutes(1));
        var recent = Directory.CreateDirectory(Path.Combine(_install.Temp, "zexoro-updater-recent")).FullName;
        _install.Stage("2.0.0", TarGzApplyTests.Archive(_install, "2.0.0"));

        Assert.Equal(UpdaterExitCodes.Success, _install.Run("apply"));

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
    }

    [Fact]
    public void WaitForExit_WaitsForAProcessThatIsNotAChild()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("kill(pid, 0) polling is the Unix path");
        }


        // sh starts sleep in the background and exits at once, so sleep is reparented and is not
        // a child of this process: no handle, no waitpid.
        var pid = StartOrphan(2);
        var watch = Stopwatch.StartNew();

        Assert.True(new SystemProcessHost().WaitForExit(pid, TimeSpan.FromSeconds(30)));

        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(25));
    }

    [Fact]
    public void WaitForExit_TimesOutWhileTheProcessRuns()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("kill(pid, 0) polling is the Unix path");
        }

        var pid = StartOrphan(60);
        try
        {
            Assert.False(new SystemProcessHost().WaitForExit(pid, TimeSpan.FromMilliseconds(300)));
        }
        finally
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
    }

    [Fact]
    public void WaitForExit_ReturnsAtOnceForAProcessThatDoesNotExist()
    {
        Assert.True(new SystemProcessHost().WaitForExit(int.MaxValue, TimeSpan.FromSeconds(30)));
    }

    /// <summary>Starts <c>sleep</c> through a shell that exits immediately; returns sleep's process id.</summary>
    private static int StartOrphan(int seconds)
    {
        var info = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add($"sleep {seconds} >/dev/null 2>&1 </dev/null & echo $!");
        using var shell = Process.Start(info)!;
        var output = new StringBuilder(shell.StandardOutput.ReadToEnd());
        shell.WaitForExit();
        return int.Parse(output.ToString().Trim(), CultureInfo.InvariantCulture);
    }
}
