using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using Zexoro.Configuration;
using Zexoro.Platforms;

namespace Zexoro.Formats.Linux.Tests;

public sealed class LinuxTarPackagerTests : IDisposable
{
    private static readonly string Key = NewKey();

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-tar-pack-");
    private readonly StringWriter _log = new();

    public LinuxTarPackagerTests()
    {
        Write("build/notes", TarFiles.Elf(62));
        Write("build/lib/libhelper.so", TarFiles.Elf(62));
        Write("build/data/readme.txt", "hello"u8.ToArray());
        Directory.CreateDirectory(Path.Combine(_dir.FullName, "build", "logs"));
        Write("app.ico", TarFiles.Ico(16, 32));
    }

    public void Dispose()
    {
        _log.Dispose();
        _dir.Delete(recursive: true);
    }

    private string Dist => Path.Combine(_dir.FullName, "dist");

    private static string NewKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    private void Write(string relative, byte[] content)
    {
        var path = Path.Combine(_dir.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private ZexoroConfig Config(string extra = "") => ZexoroConfig.Parse(
        $$"""
        [app]
        id = "com.example.notes"
        name = "Example Notes"
        version = "1.2.3"
        publisher = "Example Ltd."
        icon = "app.ico"

        [input]
        dir = "build"
        main = "notes"

        [linux]
        formats = ["tar.gz"]
        {{extra}}
        """,
        _dir.FullName);

    private const string UpdatesTable = """

        [updates]
        feed = "https://example.com/updates/{channel}/feed.json"
        download = "https://example.com/dl/{file}"
        keys = ["KEY"]
        """;

    private static UnixUpdaterBinary Updater(UpdaterRuntime runtime, byte[] bytes) =>
        new(runtime, "test updater", Convert.ToHexStringLower(SHA256.HashData(bytes)), () => new MemoryStream(bytes));

    private LinuxTarPackResult Pack(ZexoroConfig config, Architecture? architecture = null, Func<UpdaterRuntime, UnixUpdaterBinary>? resolve = null) =>
        LinuxTarPackager.Pack(new LinuxTarPackRequest
        {
            Config = config,
            Architecture = architecture ?? Architecture.X64,
            OutputDirectory = Dist,
            Log = _log,
            ResolveUpdater = resolve ?? (runtime => Updater(runtime, TarFiles.Elf(runtime.Platform.Architecture == Architecture.Arm64 ? (ushort)183 : (ushort)62))),
        });

    [Fact]
    public void Pack_WithUpdates_WritesTheLayout()
    {
        var result = Pack(Config(UpdatesTable.Replace("KEY", Key, StringComparison.Ordinal)));

        Assert.Equal(Path.Combine(Dist, "ExampleNotes-1.2.3-linux-x64.tar.gz"), result.ArchivePath);
        var entries = TarFiles.Read(result.ArchivePath).ToDictionary(e => e.Name);
        Assert.Equal(
            [
                "ExampleNotes/", "ExampleNotes/app/", "ExampleNotes/app/data/", "ExampleNotes/app/data/readme.txt", "ExampleNotes/app/lib/",
                "ExampleNotes/app/lib/libhelper.so", "ExampleNotes/app/logs/", "ExampleNotes/app/notes", "ExampleNotes/icon.png",
                "ExampleNotes/zexoro-install.json", "ExampleNotes/zexoro-updater",
            ],
            entries.Keys);
        Assert.Equal(TarItem.Executable, entries["ExampleNotes/zexoro-updater"].Mode);
        Assert.Equal(TarItem.Executable, entries["ExampleNotes/app/notes"].Mode);
        Assert.Equal(TarItem.Regular, entries["ExampleNotes/app/data/readme.txt"].Mode);
        Assert.Equal(TarItem.Regular, entries["ExampleNotes/zexoro-install.json"].Mode);
        Assert.Equal(TarFiles.Elf(62), entries["ExampleNotes/zexoro-updater"].Content);
        Assert.Equal((32, 32), (PngIcon.FromBytes(entries["ExampleNotes/icon.png"].Content).Width, PngIcon.FromBytes(entries["ExampleNotes/icon.png"].Content).Height));

        using var record = JsonDocument.Parse(entries["ExampleNotes/zexoro-install.json"].Content);
        var root = record.RootElement;
        Assert.Equal("com.example.notes", root.GetProperty("app").GetString());
        Assert.Equal("1.2.3", root.GetProperty("version").GetString());
        Assert.Equal("linux", root.GetProperty("os").GetString());
        Assert.Equal("x64", root.GetProperty("arch").GetString());
        Assert.Equal("tar-gz", root.GetProperty("format").GetString());
        Assert.Equal("user", root.GetProperty("scope").GetString());
        Assert.Equal("replace", root.GetProperty("strategy").GetString());
        Assert.Equal("https://example.com/updates/stable/feed.json", root.GetProperty("feed").GetString());
        Assert.Equal(Key, root.GetProperty("keys")[0].GetString());
        Assert.Equal("notes", root.GetProperty("main").GetString());
    }

    [Fact]
    public void Pack_WithoutUpdates_HasNoUpdaterAndAPlainRecord()
    {
        var result = Pack(Config(), resolve: _ => throw new InvalidOperationException("no updater needed"));

        var entries = TarFiles.Read(result.ArchivePath).ToDictionary(e => e.Name);
        Assert.DoesNotContain("ExampleNotes/zexoro-updater", entries.Keys);
        using var record = JsonDocument.Parse(entries["ExampleNotes/zexoro-install.json"].Content);
        Assert.False(record.RootElement.TryGetProperty("feed", out _));
        Assert.False(record.RootElement.TryGetProperty("main", out _));
    }

    [Fact]
    public void Pack_Arm64_UsesTheArm64Updater()
    {
        UpdaterRuntime? requested = null;
        var result = Pack(Config(UpdatesTable.Replace("KEY", Key, StringComparison.Ordinal)), Architecture.Arm64, runtime =>
        {
            requested = runtime;
            return Updater(runtime, TarFiles.Elf(183));
        });

        Assert.Equal(UpdaterRuntime.LinuxArm64, requested);
        Assert.EndsWith("ExampleNotes-1.2.3-linux-arm64.tar.gz", result.ArchivePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_TwiceAtDifferentTimes_GivesTheSameBytes()
    {
        var first = File.ReadAllBytes(Pack(Config()).ArchivePath);
        File.SetLastWriteTimeUtc(Path.Combine(_dir.FullName, "build", "notes"), DateTime.UtcNow.AddDays(-1));

        Assert.Equal(first, File.ReadAllBytes(Pack(Config()).ArchivePath));
    }

    [Fact]
    public void Pack_LinuxIcon_IsUsedAsItIs()
    {
        var png = PngIcon.Encode(2, 2, new byte[16]);
        Write("icon.png", png);

        var result = Pack(Config("icon = \"icon.png\""));

        Assert.Equal(png, TarFiles.Read(result.ArchivePath).Single(e => e.Name == "ExampleNotes/icon.png").Content);
    }

    [Fact]
    public void Pack_LinuxIconThatIsNoPng_Fails()
    {
        var error = Assert.Throws<TarPackException>(() => Pack(Config("icon = \"app.ico\"")));

        Assert.Contains("linux.icon", error.Message, StringComparison.Ordinal);
        Assert.Contains("is not a PNG file", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_UpdaterForTheWrongArchitecture_Fails()
    {
        var error = Assert.Throws<TarPackException>(() =>
            Pack(Config(UpdatesTable.Replace("KEY", Key, StringComparison.Ordinal)), resolve: runtime => Updater(runtime, TarFiles.Elf(183))));

        Assert.Contains("is not a linux-x64 executable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_UpdaterWithTheWrongHash_Fails()
    {
        var error = Assert.Throws<TarPackException>(() =>
            Pack(Config(UpdatesTable.Replace("KEY", Key, StringComparison.Ordinal)), resolve: runtime => new UnixUpdaterBinary(runtime, "x", new string('0', 64), () => new MemoryStream(TarFiles.Elf(62)))));

        Assert.Contains("expected 0000", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_X86_Fails() =>
        Assert.Throws<TarPackException>(() => Pack(Config(), Architecture.X86));

    [Fact]
    public void Pack_MuslMainExecutable_Warns()
    {
        Write("build/notes", TarFiles.Elf(62, "/lib/ld-musl-x86_64.so.1"));

        Pack(Config());

        Assert.Contains("warning: input.main is linked against musl (/lib/ld-musl-x86_64.so.1)", _log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_GlibcMainExecutable_DoesNotWarn()
    {
        Write("build/notes", TarFiles.Elf(62, "/lib64/ld-linux-x86-64.so.2"));

        Pack(Config());

        Assert.DoesNotContain("warning", _log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_OutputInsideTheInput_IsNotPacked()
    {
        var config = Config();
        LinuxTarPackager.Pack(new LinuxTarPackRequest { Config = config, Architecture = Architecture.X64, OutputDirectory = Path.Combine(_dir.FullName, "build", "dist") });
        var result = LinuxTarPackager.Pack(new LinuxTarPackRequest { Config = config, Architecture = Architecture.X64, OutputDirectory = Path.Combine(_dir.FullName, "build", "dist") });

        Assert.DoesNotContain(TarFiles.Read(result.ArchivePath), e => e.Name.Contains("dist", StringComparison.Ordinal));
        Assert.All(TarFiles.Read(result.ArchivePath), e => Assert.NotEqual(TarEntryType.HardLink, e.Type));
    }
}
