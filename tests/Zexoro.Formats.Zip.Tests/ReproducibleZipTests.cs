using System.Buffers.Binary;
using System.IO.Compression;

namespace Zexoro.Formats.Zip.Tests;

public sealed class ReproducibleZipTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-zip-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Source(string name)
    {
        var source = Path.Combine(_dir.FullName, name);
        Directory.CreateDirectory(Path.Combine(source, "app", "lib"));
        Directory.CreateDirectory(Path.Combine(source, "app", "empty"));
        File.WriteAllText(Path.Combine(source, "app", "Notes.exe"), "main " + new string('x', 5000));
        File.WriteAllText(Path.Combine(source, "app", "lib", "b.dll"), "b");
        File.WriteAllText(Path.Combine(source, "app", "lib", "A.dll"), "a");
        File.WriteAllText(Path.Combine(source, "app", "Ünïcødé.txt"), "u");
        File.WriteAllText(Path.Combine(source, "zexoro-install.json"), "{}");
        return source;
    }

    private string Write(string source)
    {
        var archive = Path.Combine(_dir.FullName, Guid.NewGuid().ToString("N") + ".zip");
        ReproducibleZip.WriteDirectory(source, "Notes", archive);
        return archive;
    }

    [Fact]
    public void WriteDirectory_WritesSortedEntriesBelowOneFolder()
    {
        using var zip = ZipFile.OpenRead(Write(Source("in")));

        Assert.Equal(
            [
                "Notes/", "Notes/app/", "Notes/app/Notes.exe", "Notes/app/empty/", "Notes/app/lib/", "Notes/app/lib/A.dll", "Notes/app/lib/b.dll",
                "Notes/app/Ünïcødé.txt", "Notes/zexoro-install.json",
            ],
            zip.Entries.Select(e => e.FullName));
        Assert.All(zip.Entries, e => Assert.Equal(ReproducibleZip.Timestamp.DateTime, e.LastWriteTime.DateTime));
        Assert.All(zip.Entries, e => Assert.Equal(e.FullName.EndsWith('/') ? ReproducibleZip.DirectoryAttribute : 0, e.ExternalAttributes));
        using var reader = new StreamReader(zip.GetEntry("Notes/app/Notes.exe")!.Open());
        Assert.Equal("main " + new string('x', 5000), reader.ReadToEnd());
    }

    [Fact]
    public void WriteDirectory_DependsOnlyOnNamesAndContents()
    {
        var first = Source("first");
        var second = Source("second");
        File.SetLastWriteTimeUtc(Path.Combine(second, "app", "Notes.exe"), new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(second, "app", "Notes.exe"), (UnixFileMode)0b111_101_101);
        }

        Assert.Equal(File.ReadAllBytes(Write(first)), File.ReadAllBytes(Write(second)));
    }

    [Fact]
    public void WriteDirectory_RecordsMsDosAsTheHostOnEveryHost()
    {
        var bytes = File.ReadAllBytes(Write(Source("in")));
        var count = 0;
        for (var i = 0; i + 4 <= bytes.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == 0x02014b50)
            {
                Assert.Equal(0, bytes[i + 5]);
                count++;
            }
        }

        Assert.Equal(9, count);
    }

    [Fact]
    public void WriteDirectory_PrefixMustBeOneSegment() =>
        Assert.Throws<ArgumentException>(() => ReproducibleZip.WriteDirectory(Source("in"), "a/b", Path.Combine(_dir.FullName, "x.zip")));

    [Fact]
    public void MarkMadeByMsDos_RejectsAStreamThatIsNotAZip()
    {
        using var stream = new MemoryStream(new byte[100]);
        Assert.Throws<InvalidDataException>(() => ReproducibleZip.MarkMadeByMsDos(stream));
    }
}
