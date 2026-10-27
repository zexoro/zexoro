using System.Formats.Tar;

namespace Zexoro.Formats.Linux.Tests;

public sealed class ReproducibleTarTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-tar-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string PathOf(string name) => Path.Combine(_dir.FullName, name);

    private List<TarItem> Items()
    {
        File.WriteAllText(PathOf("source.txt"), "from disk\n");
        return
        [
            TarItem.FromBytes("Root/b.txt", "b"u8.ToArray(), TarItem.Regular),
            TarItem.Directory("Root"),
            TarItem.FromFile("Root/app/data.txt", PathOf("source.txt"), TarItem.Regular),
            TarItem.Directory("Root/app"),
            TarItem.FromBytes("Root/app/run", "#!/bin/sh\n"u8.ToArray(), TarItem.Executable),
            TarItem.Link("Root/app/link", "data.txt"),
            TarItem.Directory("Root/app/empty"),
        ];
    }

    [Fact]
    public void Write_SortsEntriesAndFixesMetadata()
    {
        ReproducibleTar.Write(Items(), PathOf("a.tar.gz"));

        var entries = TarFiles.Read(PathOf("a.tar.gz"));
        Assert.Equal(["Root/", "Root/app/", "Root/app/data.txt", "Root/app/empty/", "Root/app/link", "Root/app/run", "Root/b.txt"], entries.Select(e => e.Name));
        Assert.All(entries, e =>
        {
            Assert.Equal(ReproducibleTar.Timestamp, e.Modified);
            Assert.Equal(0, e.Uid);
            Assert.Equal(0, e.Gid);
            Assert.Equal(string.Empty, e.UserName);
            Assert.Equal(string.Empty, e.GroupName);
        });
        Assert.Equal(TarEntryType.Directory, entries[0].Type);
        Assert.Equal(TarItem.Executable, entries[0].Mode);
        Assert.Equal("from disk\n", System.Text.Encoding.UTF8.GetString(entries[2].Content));
        Assert.Equal(TarItem.Regular, entries[2].Mode);
        Assert.Equal(TarEntryType.SymbolicLink, entries[4].Type);
        Assert.Equal("data.txt", entries[4].LinkName);
        Assert.Equal(TarItem.Executable, entries[5].Mode);
    }

    [Fact]
    public void Write_TwiceAtDifferentTimes_GivesTheSameBytes()
    {
        ReproducibleTar.Write(Items(), PathOf("a.tar.gz"));
        File.SetLastWriteTimeUtc(PathOf("source.txt"), DateTime.UtcNow.AddDays(-3));
        ReproducibleTar.Write(Items(), PathOf("b.tar.gz"));

        Assert.Equal(File.ReadAllBytes(PathOf("a.tar.gz")), File.ReadAllBytes(PathOf("b.tar.gz")));
    }

    [Fact]
    public void Write_GzipHeader_HasNoTimeAndNamesUnix()
    {
        ReproducibleTar.Write(Items(), PathOf("a.tar.gz"));

        var bytes = File.ReadAllBytes(PathOf("a.tar.gz"));
        Assert.Equal([0x1F, 0x8B, 8], bytes[..3]);
        Assert.Equal([0, 0, 0, 0], bytes[4..8]);
        Assert.Equal(ReproducibleTar.GzipOsUnix, bytes[ReproducibleTar.GzipOsOffset]);
    }

    [Fact]
    public void Write_LongAndNonAsciiNames_RoundTrip()
    {
        var longName = "Root/" + string.Join('/', Enumerable.Repeat("a-rather-long-directory-name", 8)) + "/file.txt";
        ReproducibleTar.Write(
            [TarItem.Directory("Root"), TarItem.FromBytes(longName, [1], TarItem.Regular), TarItem.FromBytes("Root/Ünïcødé.txt", [2], TarItem.Regular)],
            PathOf("a.tar.gz"));

        var names = TarFiles.Read(PathOf("a.tar.gz")).Select(e => e.Name).ToList();
        Assert.Contains(longName, names);
        Assert.Contains("Root/Ünïcødé.txt", names);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/abs")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("a/")]
    public void Write_InvalidName_Throws(string name) =>
        Assert.Throws<ArgumentException>(() => ReproducibleTar.Write([TarItem.FromBytes(name, [], TarItem.Regular)], PathOf("a.tar.gz")));

    [Fact]
    public void Write_DuplicateName_Throws() =>
        Assert.Throws<ArgumentException>(() => ReproducibleTar.Write([TarItem.Directory("a"), TarItem.Directory("a")], PathOf("a.tar.gz")));
}
