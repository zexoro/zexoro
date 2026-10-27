using System.Text.Json;
using Zexoro.Formats.Nsis;
using Zexoro.Versioning;

namespace Zexoro.Updater.Tests;

public sealed class ApplyTests : IDisposable
{
    private readonly TestInstall _install = new();

    public void Dispose() => _install.Dispose();

    private string Root => _install.Root;

    private FakeProcesses Processes => _install.Processes;

    private void StageInstallerWrites(string version, string main = "Notes.exe") =>
        Processes.Installer = arguments =>
        {
            Assert.Equal(["/S", "/ZEXORO_STAGE"], arguments);
            _install.WriteStagedInstall(version, main);
            return 0;
        };

    private JsonElement LastApply() =>
        JsonDocument.Parse(File.ReadAllBytes(_install.State.LastApplyFile)).RootElement;

    [Fact]
    public void Apply_ReplacesAPerUserInstall()
    {
        _install.Stage("2.0.0");
        StageInstallerWrites("2.0.0");

        var exitCode = _install.Run("apply", "--wait-pid", "77", "--restart", "--json");

        Assert.Equal(UpdaterExitCodes.Success, exitCode);
        Assert.Equal("app 2.0.0" + new string('x', 3000), File.ReadAllText(Path.Combine(Root, "app", "Notes.exe")));
        Assert.Equal("updater 2.0.0", File.ReadAllText(Path.Combine(Root, "zexoro-updater.exe")));
        Assert.Contains("\"version\": \"2.0.0\"", File.ReadAllText(Path.Combine(Root, "zexoro-install.json")), StringComparison.Ordinal);
        Assert.DoesNotContain(_install.Snapshot().Keys, k => k.Contains(".old", StringComparison.Ordinal) || k.Contains(".new", StringComparison.Ordinal));

        var entry = _install.Registry.Entries[TestInstall.App];
        Assert.Equal("2.0.0", entry.DisplayVersion);
        Assert.Equal(NsisVersionOrder.Key(SemanticVersion.Parse("2.0.0")), entry.VersionOrder);
        Assert.Equal(3u, entry.EstimatedSizeKb);

        // Waits for the app, hands off to a copy in %TEMP%, stages, swaps, restarts the app.
        Assert.Equal("wait 77", Processes.Log[0]);
        Assert.StartsWith("run zexoro-updater.exe apply --from-root " + Root + " --restart --json", Processes.Log[1], StringComparison.Ordinal);
        Assert.Equal("run Notes-2.0.0-win-x64.exe /S /ZEXORO_STAGE", Processes.Log[2]);
        var started = Assert.Single(Processes.Started);
        Assert.Equal(Path.Combine(Root, "app", "Notes.exe"), started.Path);

        // The copy reports; the handing-off updater does not print a second result.
        var json = JsonDocument.Parse(_install.Stdout.ToString().Trim()).RootElement;
        Assert.Equal("applied", json.GetProperty("status").GetString());
        Assert.Equal("1.0.0", json.GetProperty("installed").GetString());
        Assert.Equal("2.0.0", json.GetProperty("version").GetString());
        Assert.Equal("applied", LastApply().GetProperty("status").GetString());

        Assert.False(Directory.Exists(_install.State.Downloads));
        Assert.Empty(Directory.EnumerateDirectories(_install.Temp));
    }

    [Fact]
    public void Apply_ReportsNothingStaged()
    {
        Assert.Equal(UpdaterExitCodes.UpToDate, _install.Run("apply", "--json"));
        Assert.Contains("\"status\":\"up-to-date\"", _install.Stdout.ToString(), StringComparison.Ordinal);
        Assert.Empty(Processes.Log);
    }

    [Fact]
    public void Apply_DiscardsAStagedVersionThatIsNotNewer()
    {
        _install.Stage("1.0.0");
        Assert.Equal(UpdaterExitCodes.UpToDate, _install.Run("apply"));
        Assert.False(Directory.Exists(_install.State.Downloads));
    }

    [Fact]
    public void Apply_RejectsADamagedDownload()
    {
        var installer = _install.Stage("2.0.0");
        File.AppendAllText(installer, "tampered");
        var before = _install.Snapshot();
        Assert.Equal(UpdaterExitCodes.VerificationFailed, _install.Run("apply"));
        Assert.Equal(before, _install.Snapshot());
        Assert.False(File.Exists(installer));
        Assert.Empty(Processes.Log);
    }

    [Fact]
    public void Apply_GivesUpWhenTheApplicationKeepsRunning()
    {
        _install.Stage("2.0.0");
        Processes.Running.Add(77);
        Assert.Equal(UpdaterExitCodes.Cancelled, _install.Run("apply", "--wait-pid", "77"));
        Assert.Equal(["wait 77"], Processes.Log);
        Assert.Contains("did not exit", _install.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_LeavesTheInstallWhenStagingFails()
    {
        _install.Stage("2.0.0");
        var before = _install.Snapshot();
        Processes.Installer = _ =>
        {
            Directory.CreateDirectory(Path.Combine(Root, "app.new"));
            File.WriteAllText(Path.Combine(Root, "app.new", "partial"), "x");
            return 2;
        };

        Assert.Equal(UpdaterExitCodes.ApplyFailed, _install.Run("apply"));
        Assert.Equal(before, _install.Snapshot());
        Assert.Equal("1.0.0", _install.Registry.Entries[TestInstall.App].DisplayVersion);
        Assert.Contains("exited with code 2 in stage mode", _install.Stderr.ToString(), StringComparison.Ordinal);

        // A failed apply keeps the download for another try.
        Assert.True(File.Exists(_install.State.StagedFile));
    }

    [Fact]
    public void Apply_RemovesLeftoversOfAnInterruptedRun()
    {
        _install.Stage("2.0.0");
        Directory.CreateDirectory(Path.Combine(Root, "app.old", "stale"));
        File.WriteAllText(Path.Combine(Root, "zexoro-updater.exe.old"), "stale");
        File.WriteAllText(Path.Combine(Root, "uninstall.exe.new"), "stale");
        StageInstallerWrites("2.0.0");
        Assert.Equal(UpdaterExitCodes.Success, _install.Run("apply"));
        Assert.DoesNotContain(_install.Snapshot().Keys, k => k.Contains(".old", StringComparison.Ordinal) || k.Contains(".new", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_RejectsAStagedInstallForAnotherVersion()
    {
        _install.Stage("2.0.0");
        var before = _install.Snapshot();
        StageInstallerWrites("2.0.1");
        Assert.Equal(UpdaterExitCodes.ApplyFailed, _install.Run("apply"));
        Assert.Equal(before, _install.Snapshot());
    }

    [Fact]
    public void Apply_RollsBackWhenTheRegistryCannotBeUpdated()
    {
        _install.Stage("2.0.0");
        var before = _install.Snapshot();
        StageInstallerWrites("2.0.0");
        _install.Registry.FailWrites = 1;

        Assert.Equal(UpdaterExitCodes.ApplyFailed, _install.Run("apply", "--restart"));
        Assert.Equal(before, _install.Snapshot());
        Assert.Equal("1.0.0", _install.Registry.Entries[TestInstall.App].DisplayVersion);
        Assert.Empty(Processes.Started);
        Assert.Contains("rolled back", _install.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_ReportsAFailedRollback()
    {
        _install.Stage("2.0.0");
        StageInstallerWrites("2.0.0");
        _install.Registry.FailWrites = 2;
        _install.Mover = new FailAfter(8);

        Assert.Equal(UpdaterExitCodes.RollbackFailed, _install.Run("apply", "--json"));
        Assert.Contains("reinstall the application", _install.Stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(UpdaterExitCodes.RollbackFailed, LastApply().GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void Apply_RollsBackAFailedSwap()
    {
        _install.Stage("2.0.0");
        var before = _install.Snapshot();
        StageInstallerWrites("2.0.0");
        _install.Mover = new FailAt(5);

        Assert.Equal(UpdaterExitCodes.ApplyFailed, _install.Run("apply"));
        Assert.Equal(before, _install.Snapshot());
        Assert.Empty(_install.Registry.Writes);
    }

    [Fact]
    public void Apply_RunsTheInstallerWhenTheMainExecutableChanges()
    {
        _install.Stage("2.0.0");
        StageInstallerWrites("2.0.0", main: "bin/Notes2.exe");
        Processes.ElevatedInstaller = arguments =>
        {
            Assert.Equal("/S", arguments);

            // The root updater was moved aside, so the installer can write a new one.
            Assert.False(File.Exists(Path.Combine(Root, "zexoro-updater.exe")));
            File.WriteAllText(Path.Combine(Root, "zexoro-updater.exe"), "updater 2.0.0");
            File.WriteAllBytes(Path.Combine(Root, "zexoro-install.json"), _install.Record("2.0.0", "bin/Notes2.exe"));
            return 0;
        };

        Assert.Equal(UpdaterExitCodes.Success, _install.Run("apply", "--restart"));
        Assert.Contains("shell Notes-2.0.0-win-x64.exe /S", Processes.Log);
        Assert.False(Directory.Exists(Path.Combine(Root, "app.new")));
        Assert.Equal(Path.Combine(Root, "app", "bin", "Notes2.exe"), Assert.Single(Processes.Started).Path);
    }

    [Fact]
    public void Apply_RestoresTheUpdaterWhenTheFallbackInstallerFails()
    {
        _install.Stage("2.0.0");
        StageInstallerWrites("2.0.0", main: "Other.exe");
        Processes.ElevatedInstaller = _ => 1;
        Assert.Equal(UpdaterExitCodes.ApplyFailed, _install.Run("apply"));
        Assert.Equal("updater 1.0.0", File.ReadAllText(Path.Combine(Root, "zexoro-updater.exe")));
    }

    [Fact]
    public void Apply_FailsWithoutAnUninstallEntry()
    {
        _install.Stage("2.0.0");
        _install.Registry.Entries.Clear();
        Assert.Equal(UpdaterExitCodes.ApplyFailed, _install.Run("apply"));
        Assert.DoesNotContain(Processes.Log, l => l.Contains("/ZEXORO_STAGE", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_NeedsAnInstallRecord()
    {
        File.Delete(Path.Combine(Root, "zexoro-install.json"));
        Assert.Equal(UpdaterExitCodes.ManualUpdateNeeded, _install.Run("apply"));
    }

    private sealed class FailAt(int call) : IFileMover
    {
        private int _calls;

        public void Move(string source, string destination)
        {
            if (_calls++ == call)
            {
                throw new IOException("injected");
            }

            FileMover.Instance.Move(source, destination);
        }
    }

    /// <summary>Every rename after the first <c>count</c> fails.</summary>
    private sealed class FailAfter(int count) : IFileMover
    {
        private int _calls;

        public void Move(string source, string destination)
        {
            if (_calls++ >= count)
            {
                throw new IOException("injected");
            }

            FileMover.Instance.Move(source, destination);
        }
    }
}

public sealed class ReinstallTests : IDisposable
{
    private readonly TestInstall _install = new(scope: "machine");

    public void Dispose() => _install.Dispose();

    [Fact]
    public void Apply_HandsOffAndExits()
    {
        _install.Stage("2.0.0");
        Assert.Equal(UpdaterExitCodes.Success, _install.Run("apply", "--wait-pid", "77", "--restart"));
        var (path, arguments) = Assert.Single(_install.Processes.Started);
        Assert.StartsWith(_install.Temp, path, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
        Assert.Equal(["apply", "--from-root", _install.Root, "--restart", "--wait-pid", "4242"], arguments);
        Assert.Empty(_install.Stdout.ToString());
    }

    [Theory]
    [InlineData(0, UpdaterExitCodes.Success)]
    [InlineData(null, UpdaterExitCodes.Cancelled)]
    [InlineData(3, UpdaterExitCodes.UpToDate)]
    [InlineData(1, UpdaterExitCodes.ApplyFailed)]
    [InlineData(2, UpdaterExitCodes.ApplyFailed)]
    public void Apply_MapsTheInstallerExitCode(int? installerExitCode, int expected)
    {
        _install.Stage("2.0.0");
        _install.Processes.ElevatedInstaller = arguments =>
        {
            Assert.Equal("/S", arguments);
            return installerExitCode;
        };

        var copy = Path.Combine(_install.Temp, "copy", "zexoro-updater.exe");
        var exitCode = UpdaterApp.Run(
            ["apply", "--from-root", _install.Root, "--wait-pid", "4242", "--restart"], _install.Stdout, _install.Stderr, _install.Environment(copy));

        Assert.Equal(expected, exitCode);
        List<string> expectedLog = ["wait 4242", "shell Notes-2.0.0-win-x64.exe /S"];
        if (expected == 0)
        {
            expectedLog.Add("start Notes.exe");
        }

        Assert.Equal(expectedLog, _install.Processes.Log);
        Assert.Equal(expected is 0 or 1, !File.Exists(_install.State.StagedFile));

        // The per-machine installer writes the registry itself.
        Assert.Empty(_install.Registry.Writes);
    }
}
