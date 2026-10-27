using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Zexoro.Formats.Zip;

namespace Zexoro.Updater.Tests;

/// <summary><c>apply</c> for a portable zip install: extraction into <c>app.new</c>, checks, swap and rollback.</summary>
public sealed class ZipApplyTests : IDisposable
{
    private readonly TestInstall _install = new(format: "zip");
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("zexoro-zip-apply-");

    public void Dispose()
    {
        _install.Dispose();
        _work.Delete(recursive: true);
    }

    private string Root => _install.Root;

    /// <summary>An archive as <c>zexoro pack</c> writes it: <c>Notes/app/…</c>, the updater and the record.</summary>
    private byte[] PackedArchive(string version, string main = "Notes.exe", byte[]? record = null)
    {
        var staging = Path.Combine(_work.FullName, "staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(staging, "app", "lib"));
        Directory.CreateDirectory(Path.Combine(staging, "app", "empty"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(staging, "app", main))!);
        File.WriteAllText(Path.Combine(staging, "app", main), "app " + version);
        File.WriteAllText(Path.Combine(staging, "app", "lib", "data.txt"), "data " + version);
        File.WriteAllText(Path.Combine(staging, "zexoro-updater.exe"), "updater " + version);
        File.WriteAllBytes(Path.Combine(staging, "zexoro-install.json"), record ?? _install.Record(version, main));
        var archive = Path.Combine(_work.FullName, Guid.NewGuid().ToString("N") + ".zip");
        ReproducibleZip.WriteDirectory(staging, "Notes", archive);
        return File.ReadAllBytes(archive);
    }

    /// <summary>An archive with exactly the given entries (null content: a directory entry).</summary>
    private static byte[] HandmadeArchive(params (string Name, string? Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                if (content is not null)
                {
                    using var stream = entry.Open();
                    stream.Write(Encoding.UTF8.GetBytes(content));
                }
            }
        }

        return buffer.ToArray();
    }

    private JsonElement LastApply() =>
        JsonDocument.Parse(File.ReadAllBytes(_install.State.LastApplyFile)).RootElement;

    [Fact]
    public void Apply_ExtractsAndSwapsAZipInstall()
    {
        File.WriteAllText(Path.Combine(Root, "app", "obsolete.txt"), "removed by 2.0.0");
        File.WriteAllText(Path.Combine(Root, "user-notes.txt"), "kept");
        _install.Stage("2.0.0", PackedArchive("2.0.0"));

        Assert.Equal(UpdaterExitCodes.Success, _install.Run("apply", "--wait-pid", "77", "--restart", "--json"));

        Assert.Equal("app 2.0.0", File.ReadAllText(Path.Combine(Root, "app", "Notes.exe")));
        Assert.Equal("data 2.0.0", File.ReadAllText(Path.Combine(Root, "app", "lib", "data.txt")));
        Assert.True(Directory.Exists(Path.Combine(Root, "app", "empty")));
        Assert.False(File.Exists(Path.Combine(Root, "app", "obsolete.txt")));
        Assert.Equal("updater 2.0.0", File.ReadAllText(Path.Combine(Root, "zexoro-updater.exe")));
        Assert.Contains("\"version\": \"2.0.0\"", File.ReadAllText(Path.Combine(Root, "zexoro-install.json")), StringComparison.Ordinal);
        Assert.Equal("kept", File.ReadAllText(Path.Combine(Root, "user-notes.txt")));
        Assert.DoesNotContain(_install.Snapshot().Keys, k => k.Contains(".old", StringComparison.Ordinal) || k.Contains(".new", StringComparison.Ordinal));
        Assert.DoesNotContain(Directory.EnumerateFiles(Root), f => Path.GetFileName(f).StartsWith(".zexoro-write-check", StringComparison.Ordinal));

        // No installer runs and nothing is registered.
        Assert.Equal("wait 77", Processes.Log[0]);
        Assert.StartsWith("run zexoro-updater.exe apply --from-root " + Root, Processes.Log[1], StringComparison.Ordinal);
        Assert.Equal(2, Processes.Log.Count(l => !l.StartsWith("start", StringComparison.Ordinal)));
        Assert.Empty(_install.Registry.Writes);
        Assert.Empty(_install.Registry.Entries);
        Assert.Equal(Path.Combine(Root, "app", "Notes.exe"), Assert.Single(Processes.Started).Path);

        Assert.Equal("applied", LastApply().GetProperty("status").GetString());
        Assert.Equal("2.0.0", LastApply().GetProperty("version").GetString());
        Assert.False(Directory.Exists(_install.State.Downloads));
    }

    private FakeProcesses Processes => _install.Processes;

    [Fact]
    public void DownloadThenApply_StagesTheArchiveUnderItsOwnName()
    {
        _install.Publish("2.0.0", installer: PackedArchive("2.0.0"));

        Assert.Equal(UpdaterExitCodes.Success, _install.Run("download"));
        Assert.True(File.Exists(Path.Combine(_install.State.Downloads, "Notes-2.0.0-win-x64.zip")));

        Assert.Equal(UpdaterExitCodes.Success, _install.Run("apply"));
        Assert.Equal("app 2.0.0", File.ReadAllText(Path.Combine(Root, "app", "Notes.exe")));
        Assert.Equal(UpdaterExitCodes.UpToDate, _install.Run("check"));
    }

    [Fact]
    public void Apply_AcceptsAChangedMainExecutable()
    {
        _install.Stage("2.0.0", PackedArchive("2.0.0", main: "bin/Notes2.exe"));

        Assert.Equal(UpdaterExitCodes.Success, _install.Run("apply", "--restart"));

        Assert.Equal("app 2.0.0", File.ReadAllText(Path.Combine(Root, "app", "bin", "Notes2.exe")));
        Assert.Equal(Path.Combine(Root, "app", "bin", "Notes2.exe"), Assert.Single(Processes.Started).Path);
        Assert.DoesNotContain(Processes.Log, l => l.StartsWith("shell", StringComparison.Ordinal));
    }

    [Fact]
    public void Apply_RemovesLeftoversOfAnInterruptedRun()
    {
        _install.Stage("2.0.0", PackedArchive("2.0.0"));
        Directory.CreateDirectory(Path.Combine(Root, "app.new", "stale"));
        File.WriteAllText(Path.Combine(Root, "app.new", "stale", "partial.dll"), "stale");
        File.WriteAllText(Path.Combine(Root, "zexoro-updater.exe.old"), "stale");

        Assert.Equal(UpdaterExitCodes.Success, _install.Run("apply"));

        Assert.DoesNotContain(_install.Snapshot().Keys, k => k.Contains(".old", StringComparison.Ordinal) || k.Contains(".new", StringComparison.Ordinal) || k.Contains("stale", StringComparison.Ordinal));
    }

    public static TheoryData<int> EverySwapStep => [.. Enumerable.Range(0, 6)];

    [Theory]
    [MemberData(nameof(EverySwapStep))]
    public void Apply_RollsBackASwapFailingAtAnyStep(int failingStep)
    {
        _install.Stage("2.0.0", PackedArchive("2.0.0"));
        var before = _install.Snapshot();
        _install.Mover = new FailingMover(failAt: failingStep);

        Assert.Equal(UpdaterExitCodes.ApplyFailed, _install.Run("apply", "--restart"));

        Assert.Equal(before, _install.Snapshot());
        Assert.False(Directory.Exists(Path.Combine(Root, "app.new")));
        Assert.Empty(Processes.Started);
        Assert.Contains("rolled back", _install.Stderr.ToString(), StringComparison.Ordinal);

        // The download is kept for another try.
        Assert.True(File.Exists(_install.State.StagedFile));
    }

    [Fact]
    public void Apply_ReportsAFailedRollback()
    {
        _install.Stage("2.0.0", PackedArchive("2.0.0"));
        _install.Mover = new FailingMover(failAt: 3, alsoFailAt: 4);

        Assert.Equal(UpdaterExitCodes.RollbackFailed, _install.Run("apply", "--json"));

        Assert.Contains("could not be rolled back", _install.Stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(UpdaterExitCodes.RollbackFailed, LastApply().GetProperty("exitCode").GetInt32());
    }

    [Theory]
    [InlineData("com.example.other", "2.0.0", "zip", "user")]
    [InlineData(TestInstall.App, "2.0.1", "zip", "user")]
    [InlineData(TestInstall.App, "2.0.0", "nsis", "user")]
    [InlineData(TestInstall.App, "2.0.0", "zip", "machine")]
    public void Apply_RejectsAnArchiveForAnotherInstall(string app, string version, string format, string scope)
    {
        var record = Encoding.UTF8.GetString(_install.Record(version, app: app))
            .Replace("\"format\": \"zip\"", $"\"format\": \"{format}\"", StringComparison.Ordinal)
            .Replace("\"scope\": \"user\"", $"\"scope\": \"{scope}\"", StringComparison.Ordinal);
        _install.Stage("2.0.0", PackedArchive("2.0.0", record: Encoding.UTF8.GetBytes(record)));
        var before = _install.Snapshot();

        Assert.Equal(UpdaterExitCodes.ApplyFailed, _install.Run("apply"));

        Assert.Equal(before, _install.Snapshot());
        Assert.Contains("the update archive holds", _install.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_RejectsAnArchiveWithoutARecord()
    {
        _install.Stage("2.0.0", HandmadeArchive(("Notes/app/Notes.exe", "app 2.0.0")));
        var before = _install.Snapshot();

        Assert.Equal(UpdaterExitCodes.ApplyFailed, _install.Run("apply"));

        Assert.Equal(before, _install.Snapshot());
        Assert.Contains("no valid install record", _install.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Notes/app/../../evil.txt", "unsafe path")]
    [InlineData("Notes/../evil.txt", "unsafe path")]
    [InlineData("/etc/evil.txt", "unsafe path")]
    [InlineData("C:/evil.txt", "unsafe path")]
    [InlineData("Notes\\app\\evil.txt", "unsafe path")]
    [InlineData("Notes/app//evil.txt", "unsafe path")]
    [InlineData("Notes/app/./evil.txt", "unsafe path")]
    [InlineData("Other/app/evil.txt", "more than one top-level folder")]
    [InlineData("evil.txt", "more than one top-level folder")]
    [InlineData("Notes/lib/evil.dll", "unexpected item")]
    [InlineData("Notes/app", "unexpected item")]
    [InlineData("Notes/app/Notes.exe", "twice")]
    [InlineData("Notes/APP/notes.exe", "twice")]
    public void Apply_RejectsAMaliciousArchive(string name, string reason)
    {
        _install.Stage("2.0.0", HandmadeArchive(
            ("Notes/app/Notes.exe", "app 2.0.0"),
            ("Notes/zexoro-install.json", Encoding.UTF8.GetString(_install.Record("2.0.0"))),
            (name, "evil")));
        var before = _install.Snapshot();

        Assert.Equal(UpdaterExitCodes.VerificationFailed, _install.Run("apply", "--json"));

        Assert.Equal(before, _install.Snapshot());
        Assert.False(File.Exists(Path.Combine(_install.Root, "..", "evil.txt")));
        Assert.Contains(reason, _install.Stderr.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(_install.State.Downloads));
    }

    [Fact]
    public void Apply_RejectsALink()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("Notes/app/Notes.exe");
            var link = zip.CreateEntry("Notes/app/escape");
            link.ExternalAttributes = unchecked((int)0xA1FF0000); // S_IFLNK | 0777
            using var target = link.Open();
            target.Write("../../../outside"u8);
        }

        _install.Stage("2.0.0", buffer.ToArray());
        var before = _install.Snapshot();

        Assert.Equal(UpdaterExitCodes.VerificationFailed, _install.Run("apply"));

        Assert.Equal(before, _install.Snapshot());
        Assert.Contains("link", _install.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_RejectsSomethingThatIsNotAZip()
    {
        _install.Stage("2.0.0", "MZ not a zip"u8.ToArray());
        var before = _install.Snapshot();

        Assert.Equal(UpdaterExitCodes.VerificationFailed, _install.Run("apply"));

        Assert.Equal(before, _install.Snapshot());
        Assert.Contains("not a valid zip archive", _install.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_ReportsAnInstallFolderThatIsNotWritable()
    {
        _install.Stage("2.0.0", PackedArchive("2.0.0"));
        var before = _install.Snapshot();
        using var readOnly = ReadOnlyDirectory.Apply(Root);
        if (!readOnly.Effective)
        {
            Assert.Skip("cannot make a directory read-only for this user (running as root?)");
        }

        Assert.Equal(UpdaterExitCodes.ManualUpdateNeeded, _install.Run("apply", "--json"));

        readOnly.Dispose();
        Assert.Equal(before, _install.Snapshot());
        Assert.Contains("is not writable", _install.Stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(UpdaterExitCodes.ManualUpdateNeeded, LastApply().GetProperty("exitCode").GetInt32());

        // The download is kept: once the folder is writable, apply works.
        Assert.Equal(UpdaterExitCodes.Success, _install.Run("apply"));
        Assert.Equal("app 2.0.0", File.ReadAllText(Path.Combine(Root, "app", "Notes.exe")));
    }
}

/// <summary>Takes away the right to create files in a directory: mode 0555 on Unix, a deny ACE on Windows.</summary>
internal sealed class ReadOnlyDirectory : IDisposable
{
    private readonly string _path;
    private readonly Action _restore;
    private bool _restored;

    private ReadOnlyDirectory(string path, Action restore)
    {
        _path = path;
        _restore = restore;
    }

    /// <summary>Whether creating a file in the directory now fails.</summary>
    public bool Effective
    {
        get
        {
            try
            {
                File.WriteAllText(Path.Combine(_path, "probe"), "x");
                File.Delete(Path.Combine(_path, "probe"));
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }
    }

    public static ReadOnlyDirectory Apply(string path) => OperatingSystem.IsWindows() ? DenyCreate(path) : RemoveWriteMode(path);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static ReadOnlyDirectory DenyCreate(string path)
    {
        var directory = new DirectoryInfo(path);
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            user,
            System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.CreateDirectories,
            System.Security.AccessControl.AccessControlType.Deny);
        var security = directory.GetAccessControl();
        security.AddAccessRule(rule);
        directory.SetAccessControl(security);
        return new ReadOnlyDirectory(path, () =>
        {
            var restored = directory.GetAccessControl();
            restored.RemoveAccessRule(rule);
            directory.SetAccessControl(restored);
        });
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static ReadOnlyDirectory RemoveWriteMode(string path)
    {
        var mode = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return new ReadOnlyDirectory(path, () => File.SetUnixFileMode(path, mode));
    }

    public void Dispose()
    {
        if (!_restored)
        {
            _restored = true;
            _restore();
        }
    }
}
