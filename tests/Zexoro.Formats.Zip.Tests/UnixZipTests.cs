using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;

namespace Zexoro.Formats.Zip.Tests;

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class UnixZipTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-unixzip-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Source => Path.Combine(_dir.FullName, "Notes.app");

    private void Arrange()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix modes and symbolic links.");
        Directory.CreateDirectory(Path.Combine(Source, "Contents", "MacOS"));
        Directory.CreateDirectory(Path.Combine(Source, "Contents", "Resources", "empty"));
        var main = Path.Combine(Source, "Contents", "MacOS", "Notes");
        File.WriteAllText(main, "main");
        File.SetUnixFileMode(main, (UnixFileMode)0x1C0);
        File.WriteAllText(Path.Combine(Source, "Contents", "Resources", "data.txt"), "data");
        File.CreateSymbolicLink(Path.Combine(Source, "Contents", "MacOS", "data.txt"), "../Resources/data.txt");
    }

    [Fact]
    public void WriteDirectory_StoresUnixModesLinksAndTheUnixHost()
    {
        Arrange();
        var zip = Path.Combine(_dir.FullName, "notes.zip");

        UnixZip.WriteDirectory(Source, "Notes.app", zip);

        using (var archive = ZipFile.OpenRead(zip))
        {
            var entries = archive.Entries.ToDictionary(e => e.FullName, e => (e.ExternalAttributes >> 16) & 0xFFFF);
            Assert.Equal(
                ["Notes.app/", "Notes.app/Contents/", "Notes.app/Contents/MacOS/", "Notes.app/Contents/MacOS/Notes", "Notes.app/Contents/MacOS/data.txt",
                    "Notes.app/Contents/Resources/", "Notes.app/Contents/Resources/data.txt", "Notes.app/Contents/Resources/empty/"],
                archive.Entries.Select(e => e.FullName));
            Assert.Equal(0x41ED, entries["Notes.app/Contents/"]);
            Assert.Equal(0x81ED, entries["Notes.app/Contents/MacOS/Notes"]);
            Assert.Equal(0x81A4, entries["Notes.app/Contents/Resources/data.txt"]);
            Assert.Equal(0xA1ED, entries["Notes.app/Contents/MacOS/data.txt"]);
            using var link = new StreamReader(archive.GetEntry("Notes.app/Contents/MacOS/data.txt")!.Open());
            Assert.Equal("../Resources/data.txt", link.ReadToEnd());
        }

        // Every central directory header says "made by Unix" (host 3), which unzip tools need to apply the modes.
        var bytes = File.ReadAllBytes(zip);
        var headers = 0;
        for (var i = 0; i + 4 <= bytes.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == 0x02014b50)
            {
                Assert.Equal(3, bytes[i + 5]);
                headers++;
            }
        }

        Assert.Equal(8, headers);
    }

    [Fact]
    public void WriteDirectory_IsRestoredByDitto()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "ditto exists on macOS only.");
        Arrange();
        var zip = Path.Combine(_dir.FullName, "notes.zip");
        UnixZip.WriteDirectory(Source, "Notes.app", zip);
        var output = Directory.CreateDirectory(Path.Combine(_dir.FullName, "out")).FullName;

        using (var ditto = Process.Start("/usr/bin/ditto", ["-x", "-k", zip, output]))
        {
            ditto.WaitForExit();
            Assert.Equal(0, ditto.ExitCode);
        }

        var restored = Path.Combine(output, "Notes.app");
        Assert.Equal("../Resources/data.txt", new FileInfo(Path.Combine(restored, "Contents", "MacOS", "data.txt")).LinkTarget);
        Assert.Equal((UnixFileMode)0x1ED, File.GetUnixFileMode(Path.Combine(restored, "Contents", "MacOS", "Notes")) & (UnixFileMode)0x1FF);
        Assert.Equal((UnixFileMode)0x1A4, File.GetUnixFileMode(Path.Combine(restored, "Contents", "Resources", "data.txt")) & (UnixFileMode)0x1FF);
        Assert.True(Directory.Exists(Path.Combine(restored, "Contents", "Resources", "empty")));
    }
}
