using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Zexoro.Toolsets;

namespace Zexoro.Core.Tests;

public sealed class ToolsetCacheTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("zexoro-toolset-");

    public void Dispose() => _root.Delete(recursive: true);

    private static byte[] BuildArchive(Action<TarWriter>? extra = null)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./"));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "./Bin/"));
            AddFile(tar, "./Bin/tool", "#!/bin/sh\necho tool\n", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            AddFile(tar, "./Include/a.nsh", "; include\n");
            AddFile(tar, "./Docs/manual.html", "<p>docs</p>");
            AddFile(tar, "COPYING", "license");
            extra?.Invoke(tar);
        }

        return buffer.ToArray();
    }

    private static void AddFile(TarWriter tar, string name, string content, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead)
    {
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            Mode = mode,
        });
    }

    private static ToolsetArchive Archive(byte[] bytes, Action? onOpen = null) =>
        new("test", "1.0-zexoro.1", Convert.ToHexStringLower(SHA256.HashData(bytes)), () =>
        {
            onOpen?.Invoke();
            return new MemoryStream(bytes);
        });

    private static bool NotDocs(string path) => !path.StartsWith("Docs/", StringComparison.Ordinal) && path != "Docs";

    [Fact]
    public void Install_ExtractsFilteredFilesIntoVersionedDirectory()
    {
        var archive = Archive(BuildArchive());
        var directory = ToolsetCache.Install(archive, _root.FullName, NotDocs);

        Assert.Equal(Path.Combine(_root.FullName, "toolsets", archive.DirectoryName), directory);
        Assert.StartsWith("test-1.0-zexoro.1-", Path.GetFileName(directory), StringComparison.Ordinal);
        Assert.Equal("#!/bin/sh\necho tool\n", File.ReadAllText(Path.Combine(directory, "Bin", "tool")));
        Assert.True(File.Exists(Path.Combine(directory, "Include", "a.nsh")));
        Assert.True(File.Exists(Path.Combine(directory, "COPYING")));
        Assert.False(Directory.Exists(Path.Combine(directory, "Docs")));
        Assert.True(File.Exists(Path.Combine(directory, ToolsetCache.ManifestFileName)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(Path.Combine(directory, "Bin", "tool")).HasFlag(UnixFileMode.UserExecute));
        }

        Assert.Empty(Directory.GetDirectories(Path.Combine(_root.FullName, "toolsets"), ".tmp-*"));
    }

    [Fact]
    public void Install_Twice_ReusesTheExtractedDirectory()
    {
        var opened = 0;
        var archive = Archive(BuildArchive(), () => opened++);
        var first = ToolsetCache.Install(archive, _root.FullName);
        var second = ToolsetCache.Install(archive, _root.FullName);
        Assert.Equal(first, second);
        Assert.Equal(1, opened);
    }

    [Fact]
    public void Install_ModifiedFile_ExtractsAgain()
    {
        var opened = 0;
        var archive = Archive(BuildArchive(), () => opened++);
        var directory = ToolsetCache.Install(archive, _root.FullName);
        File.WriteAllText(Path.Combine(directory, "Include", "a.nsh"), "tampered");

        Assert.False(ToolsetCache.Verify(directory, archive, out var problem));
        Assert.Contains("Include/a.nsh", problem, StringComparison.Ordinal);
        Assert.Equal(directory, ToolsetCache.Install(archive, _root.FullName));
        Assert.Equal("; include\n", File.ReadAllText(Path.Combine(directory, "Include", "a.nsh")));
        Assert.Equal(2, opened);
    }

    [Fact]
    public void Install_DeletedFileOrManifest_ExtractsAgain()
    {
        var archive = Archive(BuildArchive());
        var directory = ToolsetCache.Install(archive, _root.FullName);
        File.Delete(Path.Combine(directory, "Bin", "tool"));
        ToolsetCache.Install(archive, _root.FullName);
        Assert.True(File.Exists(Path.Combine(directory, "Bin", "tool")));

        File.Delete(Path.Combine(directory, ToolsetCache.ManifestFileName));
        ToolsetCache.Install(archive, _root.FullName);
        Assert.True(ToolsetCache.Verify(directory, archive, out _));
    }

    [Fact]
    public void Install_IncompleteDirectoryFromAnotherRun_IsReplaced()
    {
        var archive = Archive(BuildArchive());
        var directory = Path.Combine(_root.FullName, "toolsets", archive.DirectoryName);
        Directory.CreateDirectory(Path.Combine(directory, "Bin"));
        File.WriteAllText(Path.Combine(directory, "Bin", "tool"), "partial");

        Assert.Equal(directory, ToolsetCache.Install(archive, _root.FullName));
        Assert.True(ToolsetCache.Verify(directory, archive, out _));
    }

    [Fact]
    public async Task Install_Concurrently_AllCallersGetTheSameVerifiedDirectory()
    {
        var bytes = BuildArchive(tar =>
        {
            for (var i = 0; i < 200; i++)
            {
                AddFile(tar, $"Contrib/file{i}.txt", new string('x', 1000 + i));
            }
        });
        var archive = Archive(bytes);
        using var start = new Barrier(8);
        var results = Enumerable.Range(0, 8)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    start.SignalAndWait();
                    return ToolsetCache.Install(archive, _root.FullName);
                },
                TaskCreationOptions.LongRunning))
            .ToArray();
        var directories = await Task.WhenAll(results);

        Assert.Single(directories.Distinct());
        Assert.True(ToolsetCache.Verify(directories[0], archive, out _));
        var leftovers = Directory.GetDirectories(Path.Combine(_root.FullName, "toolsets"))
            .Select(Path.GetFileName)
            .Where(n => n!.StartsWith('.'));
        Assert.Empty(leftovers);
    }

    [Fact]
    public void Install_HashMismatch_Throws()
    {
        var bytes = BuildArchive();
        var archive = Archive(bytes) with { Sha256 = new string('0', 64) };
        var ex = Assert.Throws<ToolsetException>(() => ToolsetCache.Install(archive, _root.FullName));
        Assert.Contains("corrupt", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root.FullName, "toolsets", archive.DirectoryName)));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("Bin/../../evil")]
    [InlineData("/etc/evil")]
    public void Install_UnsafePath_Throws(string name)
    {
        var archive = Archive(BuildArchive(tar => AddFile(tar, name, "x")));
        Assert.Throws<ToolsetException>(() => ToolsetCache.Install(archive, _root.FullName));
        Assert.False(File.Exists(Path.Combine(_root.FullName, "evil")));
    }

    [Fact]
    public void Install_SymbolicLink_Throws()
    {
        var archive = Archive(BuildArchive(tar => tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "Bin/link") { LinkName = "/etc/passwd" })));
        Assert.Throws<ToolsetException>(() => ToolsetCache.Install(archive, _root.FullName));
    }

    [Fact]
    public void Install_NotGzip_Throws()
    {
        var bytes = "not an archive"u8.ToArray();
        Assert.Throws<ToolsetException>(() => ToolsetCache.Install(Archive(bytes), _root.FullName));
    }

    [Fact]
    public void Install_InvalidName_Throws() =>
        Assert.Throws<ArgumentException>(() => ToolsetCache.Install(Archive(BuildArchive()) with { Version = "../x" }, _root.FullName));

    [Fact]
    public void GetDefaultRoot_Override()
    {
        var root = ToolsetCache.GetDefaultRoot(name => name == ToolsetCache.CacheDirectoryVariable ? _root.FullName : null);
        Assert.Equal(_root.FullName, root);
    }

    [Fact]
    public void GetDefaultRoot_PlatformDefault()
    {
        var home = Path.Combine(_root.FullName, "home");
        string? Variables(string name) => name switch
        {
            "HOME" => home,
            "LOCALAPPDATA" => Path.Combine(home, "Local"),
            _ => null,
        };

        var expected = OperatingSystem.IsWindows() ? Path.Combine(home, "Local", "zexoro")
            : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Caches", "zexoro")
            : Path.Combine(home, ".cache", "zexoro");
        Assert.Equal(expected, ToolsetCache.GetDefaultRoot(Variables));
    }

    [Fact]
    public void GetDefaultRoot_XdgCacheHome()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return;
        }

        var xdg = Path.Combine(_root.FullName, "xdg");
        Assert.Equal(Path.Combine(xdg, "zexoro"), ToolsetCache.GetDefaultRoot(name => name == "XDG_CACHE_HOME" ? xdg : null));
    }
}
