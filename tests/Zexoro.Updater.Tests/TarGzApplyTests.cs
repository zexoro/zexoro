using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Zexoro.Updater.Tests;

/// <summary>
/// <c>apply</c> for a portable tar.gz install (docs/design/0006-desktop-formats.md, "replace — Windows
/// zip, tar.gz"): Unix modes and symbolic links survive, unsafe links are refused. The archives are
/// written with <see cref="TarWriter"/>; the tar.gz packager comes with step 5.
/// </summary>
public sealed class TarGzApplyTests : IDisposable
{
    private const UnixFileMode Executable = (UnixFileMode)0x1ED; // 0755
    private const UnixFileMode Data = (UnixFileMode)0x1A4; // 0644

    private readonly TestInstall _install = new(format: "tar-gz", main: "Notes");

    public void Dispose() => _install.Dispose();

    private string Root => _install.Root;

    /// <summary>One tar entry: a file (content), directory (null content, no link) or symbolic link.</summary>
    internal sealed record Entry(string Name, string? Content = null, string? Link = null, UnixFileMode? Mode = null, TarEntryType? Type = null);

    /// <summary>A tar.gz with exactly <paramref name="entries"/>, in order.</summary>
    internal static byte[] Write(params Entry[] entries)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var e in entries)
            {
                var type = e.Type ?? (e.Link is not null ? TarEntryType.SymbolicLink : e.Content is null ? TarEntryType.Directory : TarEntryType.RegularFile);
                var entry = new PaxTarEntry(type, e.Name);
                if (e.Mode is { } mode)
                {
                    entry.Mode = mode;
                }

                if (e.Link is not null)
                {
                    entry.LinkName = e.Link;
                }

                if (e.Content is not null)
                {
                    entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(e.Content));
                }

                tar.WriteEntry(entry);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>A Linux portable archive as step 5's packager will write it, with links in <c>app/</c>.</summary>
    internal static byte[] Archive(TestInstall install, string version, params Entry[] extra) => Write(
        [
            new("Notes/", Mode: Executable),
            new("Notes/app/", Mode: Executable),
            new("Notes/app/Notes", "app " + version, Mode: Executable),
            new("Notes/app/bin/", Mode: (UnixFileMode)0x1E8), // 0750
            new("Notes/app/bin/notes", Link: "../Notes"),
            new("Notes/app/lib/", Mode: Executable),
            new("Notes/app/lib/data.txt", "data " + version, Mode: Data),
            new("Notes/app/lib/libnotes.so.1", "library " + version, Mode: Executable),
            new("Notes/app/lib/libnotes.so", Link: "libnotes.so.1"),
            new("Notes/app/lib/current", Link: "."),
            new("Notes/zexoro-updater", "updater " + version, Mode: Executable),
            new("Notes/zexoro-install.json", Encoding.UTF8.GetString(install.Record(version, "Notes")), Mode: Data),
            .. extra,
        ]);

    [Fact]
    public void Apply_KeepsModesAndLinks()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix modes and symbolic links");
            return;
        }

        File.WriteAllText(Path.Combine(Root, "app", "obsolete.txt"), "removed by 2.0.0");
        _install.Stage("2.0.0", Archive(_install, "2.0.0"));

        Assert.Equal(UpdaterExitCodes.Success, _install.Run("apply", "--wait-pid", "77", "--restart"));

        Assert.Equal("app 2.0.0", File.ReadAllText(Path.Combine(Root, "app", "Notes")));
        Assert.Equal(Executable, File.GetUnixFileMode(Path.Combine(Root, "app", "Notes")));
        Assert.Equal(Data, File.GetUnixFileMode(Path.Combine(Root, "app", "lib", "data.txt")));
        Assert.Equal((UnixFileMode)0x1E8, File.GetUnixFileMode(Path.Combine(Root, "app", "bin")));
        Assert.Equal(Executable, File.GetUnixFileMode(Path.Combine(Root, "zexoro-updater")));
        Assert.Equal(Data, File.GetUnixFileMode(Path.Combine(Root, "zexoro-install.json")));

        Assert.Equal("../Notes", new FileInfo(Path.Combine(Root, "app", "bin", "notes")).LinkTarget);
        Assert.Equal("app 2.0.0", File.ReadAllText(Path.Combine(Root, "app", "bin", "notes")));
        Assert.Equal("library 2.0.0", File.ReadAllText(Path.Combine(Root, "app", "lib", "libnotes.so")));
        Assert.Equal("data 2.0.0", File.ReadAllText(Path.Combine(Root, "app", "lib", "current", "data.txt")));

        Assert.False(File.Exists(Path.Combine(Root, "app", "obsolete.txt")));
        Assert.Equal("updater 2.0.0", File.ReadAllText(Path.Combine(Root, "zexoro-updater")));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(Root), e => e.EndsWith(".old", StringComparison.Ordinal) || e.EndsWith(".new", StringComparison.Ordinal));
        Assert.Equal(Path.Combine(Root, "app", "Notes"), Assert.Single(_install.Processes.Started).Path);
        Assert.False(Directory.Exists(_install.State.Downloads));
    }

    [Fact]
    public void Apply_RestoresWriteAccessToReadOnlyDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix modes");
            return;
        }

        _install.Stage("2.0.0", Archive(_install, "2.0.0", new Entry("Notes/app/share/", Mode: (UnixFileMode)0x16D), new Entry("Notes/app/share/x.txt", "x", Mode: Data)));

        Assert.Equal(UpdaterExitCodes.Success, _install.Run("apply"));

        // 0555 in the archive; the owner keeps write access so the next update can remove it.
        Assert.Equal((UnixFileMode)0x1ED, File.GetUnixFileMode(Path.Combine(Root, "app", "share")));
    }

    [Fact]
    public void DownloadThenApply_StagesTheArchiveUnderItsOwnName()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("symbolic links need privileges on Windows");
            return;
        }

        _install.Publish("2.0.0", installer: Archive(_install, "2.0.0"));

        Assert.Equal(UpdaterExitCodes.Success, _install.Run("download"));
        Assert.True(File.Exists(Path.Combine(_install.State.Downloads, "Notes-2.0.0-win-x64.tar.gz")));

        Assert.Equal(UpdaterExitCodes.Success, _install.Run("apply"));
        Assert.Equal("app 2.0.0", File.ReadAllText(Path.Combine(Root, "app", "Notes")));
        Assert.Equal(UpdaterExitCodes.UpToDate, _install.Run("check"));
    }

    public static TheoryData<string, string, string> UnsafeLinks => new()
    {
        { "Notes/app/escape", "../../outside", "points outside the app folder" },
        { "Notes/app/escape", "..", "points outside the app folder" },
        { "Notes/app/lib/escape", "../../zexoro-install.json", "points outside the app folder" },
        { "Notes/app/absolute", "/etc/passwd", "points outside the app folder" },
        { "Notes/app/empty-segment", "lib//data.txt", "points outside the app folder" },
        { "Notes/app/backslash", "..\\..\\outside", "points outside the app folder" },
        { "Notes/app/through", "lib/current/../../..", "passes through another link" },
        { "Notes/zexoro-updater-link", "app/Notes", "unexpected item" },
        { "Notes/app", "lib", "unexpected item" },
    };

    [Theory]
    [MemberData(nameof(UnsafeLinks))]
    public void Apply_RejectsAnUnsafeLink(string name, string target, string reason)
    {
        _install.Stage("2.0.0", Archive(_install, "2.0.0", new Entry(name, Link: target)));
        var before = _install.Snapshot();

        Assert.Equal(UpdaterExitCodes.VerificationFailed, _install.Run("apply"));

        Assert.Equal(before, _install.Snapshot());
        Assert.False(Directory.Exists(Path.Combine(Root, "app.new")));
        Assert.Contains(reason, _install.Stderr.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(_install.State.Downloads));
    }

    [Fact]
    public void Apply_RejectsAnEntryBelowALink()
    {
        // lib/current is a link to lib; writing lib/current/x would go through it.
        _install.Stage("2.0.0", Archive(_install, "2.0.0", new Entry("Notes/app/lib/current/x.txt", "x")));
        var before = _install.Snapshot();

        Assert.Equal(UpdaterExitCodes.VerificationFailed, _install.Run("apply"));

        Assert.Equal(before, _install.Snapshot());
        Assert.Contains("lies below a link", _install.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TarEntryType.HardLink, "hard link")]
    [InlineData(TarEntryType.Fifo, "Fifo")]
    [InlineData(TarEntryType.CharacterDevice, "CharacterDevice")]
    public void Apply_RejectsSpecialEntries(TarEntryType type, string reason)
    {
        _install.Stage("2.0.0", Archive(_install, "2.0.0", new Entry("Notes/app/special", Link: type == TarEntryType.HardLink ? "Notes/app/Notes" : null, Type: type)));
        var before = _install.Snapshot();

        Assert.Equal(UpdaterExitCodes.VerificationFailed, _install.Run("apply"));

        Assert.Equal(before, _install.Snapshot());
        Assert.Contains(reason, _install.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Notes/app/../../evil.txt", "unsafe path")]
    [InlineData("/etc/evil.txt", "unsafe path")]
    [InlineData("Notes/app/./evil.txt", "unsafe path")]
    [InlineData("Other/app/evil.txt", "more than one top-level folder")]
    [InlineData("Notes/lib/evil.so", "unexpected item")]
    [InlineData("Notes/app/Notes", "twice")]
    public void Apply_RejectsAMaliciousArchive(string name, string reason)
    {
        _install.Stage("2.0.0", Archive(_install, "2.0.0", new Entry(name, "evil")));
        var before = _install.Snapshot();

        Assert.Equal(UpdaterExitCodes.VerificationFailed, _install.Run("apply"));

        Assert.Equal(before, _install.Snapshot());
        Assert.Contains(reason, _install.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_ComparesNamesExactly()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "names that differ only in case need a case-sensitive file system");
        _install.Stage("2.0.0", Archive(_install, "2.0.0", new Entry("Notes/app/notes", "lower case")));

        Assert.Equal(UpdaterExitCodes.Success, _install.Run("apply"));

        Assert.Equal("lower case", File.ReadAllText(Path.Combine(Root, "app", "notes")));
    }

    [Theory]
    [InlineData("not gzip at all")]
    [InlineData("gzip, not tar")]
    public void Apply_RejectsSomethingThatIsNotATarGz(string kind)
    {
        byte[] bytes;
        if (kind == "not gzip at all")
        {
            bytes = "MZ not an archive"u8.ToArray();
        }
        else
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(Encoding.ASCII.GetBytes(new string('x', 1024)));
            }

            bytes = buffer.ToArray();
        }

        _install.Stage("2.0.0", bytes);
        var before = _install.Snapshot();

        Assert.Equal(UpdaterExitCodes.VerificationFailed, _install.Run("apply"));

        Assert.Equal(before, _install.Snapshot());
        Assert.Contains("not a valid tar.gz archive", _install.Stderr.ToString(), StringComparison.Ordinal);
    }
}
