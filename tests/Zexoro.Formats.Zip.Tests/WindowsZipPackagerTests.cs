using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Zexoro.Configuration;
using Zexoro.Formats.Nsis;
using Zexoro.Platforms;
using Zexoro.Signing;
using Zexoro.Signing.Fixtures;

namespace Zexoro.Formats.Zip.Tests;

public sealed class WindowsZipPackagerTests : IDisposable
{
    private static readonly string Key = NewKey();

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-zip-pack-");
    private readonly byte[] _updater = TestPe.Build(pe32Plus: true, seed: 9);

    public WindowsZipPackagerTests()
    {
        Write("build/Notes.exe", TestPe.Build(pe32Plus: true));
        Write("build/lib/helper.dll", TestPe.Build(pe32Plus: true, seed: 2));
        Write("build/data/readme.txt", "hello"u8.ToArray());
        Directory.CreateDirectory(Path.Combine(_dir.FullName, "build", "logs"));
    }

    public void Dispose() => _dir.Delete(recursive: true);

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
        main = "Notes.exe"

        [windows]
        formats = ["zip"]
        {{extra}}
        """,
        _dir.FullName);

    private const string UpdatesTable = """

        [updates]
        feed = "https://example.com/updates/{channel}/feed.json"
        download = "https://example.com/dl/{file}"
        keys = ["KEY"]
        """;

    private WindowsZipPackResult Pack(ZexoroConfig config, NsisSigning? signing = null, StringWriter? log = null) =>
        WindowsZipPackager.Pack(new WindowsZipPackRequest
        {
            Config = config,
            Architecture = Architecture.X64,
            OutputDirectory = Dist,
            Signing = signing,
            Log = log,
            ResolveUpdater = architecture => new UpdaterBinary(
                architecture, "test updater", Convert.ToHexStringLower(SHA256.HashData(_updater)), () => new MemoryStream(_updater)),
        });

    private static Dictionary<string, byte[]> Read(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        return zip.Entries.ToDictionary(
            e => e.FullName,
            e =>
            {
                using var buffer = new MemoryStream();
                using (var stream = e.Open())
                {
                    stream.CopyTo(buffer);
                }

                return buffer.ToArray();
            });
    }

    private static JsonElement Record(Dictionary<string, byte[]> entries) =>
        JsonDocument.Parse(entries["ExampleNotes/zexoro-install.json"]).RootElement;

    [Fact]
    public void Pack_WithUpdates_WritesTheLayoutAndRecord()
    {
        var result = Pack(Config(UpdatesTable.Replace("KEY", Key, StringComparison.Ordinal)));

        Assert.Equal(Path.Combine(Dist, "ExampleNotes-1.2.3-win-x64.zip"), result.ArchivePath);
        var entries = Read(result.ArchivePath);
        Assert.Equal(
            [
                "ExampleNotes/", "ExampleNotes/app/", "ExampleNotes/app/Notes.exe", "ExampleNotes/app/data/", "ExampleNotes/app/data/readme.txt",
                "ExampleNotes/app/lib/", "ExampleNotes/app/lib/helper.dll", "ExampleNotes/app/logs/", "ExampleNotes/zexoro-install.json",
                "ExampleNotes/zexoro-updater.exe",
            ],
            entries.Keys);
        Assert.Equal(_updater, entries["ExampleNotes/zexoro-updater.exe"]);
        Assert.Equal(File.ReadAllBytes(Path.Combine(_dir.FullName, "build", "Notes.exe")), entries["ExampleNotes/app/Notes.exe"]);

        var record = Record(entries);
        Assert.Equal(1, record.GetProperty("schema").GetInt32());
        Assert.Equal("com.example.notes", record.GetProperty("app").GetString());
        Assert.Equal("1.2.3", record.GetProperty("version").GetString());
        Assert.Equal("windows", record.GetProperty("os").GetString());
        Assert.Equal("x64", record.GetProperty("arch").GetString());
        Assert.Equal("zip", record.GetProperty("format").GetString());
        Assert.Equal("user", record.GetProperty("scope").GetString());
        Assert.Equal("replace", record.GetProperty("strategy").GetString());
        Assert.Equal("stable", record.GetProperty("channel").GetString());
        Assert.Equal("https://example.com/updates/stable/feed.json", record.GetProperty("feed").GetString());
        Assert.Equal(Key, Assert.Single(record.GetProperty("keys").EnumerateArray()).GetString());
        Assert.Equal("Notes.exe", record.GetProperty("main").GetString());
    }

    [Fact]
    public void Pack_MachineScopeConfigured_IsStillAPerUserZip()
    {
        var record = Record(Read(Pack(Config("install_scope = \"machine\"")).ArchivePath));
        Assert.Equal("user", record.GetProperty("scope").GetString());
        Assert.Equal("replace", record.GetProperty("strategy").GetString());
    }

    [Fact]
    public void Pack_WithoutUpdates_HasNoUpdaterAndNoUpdateSettings()
    {
        var entries = Read(Pack(Config()).ArchivePath);

        Assert.DoesNotContain("ExampleNotes/zexoro-updater.exe", entries.Keys);
        var record = Record(entries);
        Assert.Equal("zip", record.GetProperty("format").GetString());
        Assert.False(record.TryGetProperty("feed", out _));
        Assert.False(record.TryGetProperty("main", out _));
    }

    [Fact]
    public void Pack_IsReproducible()
    {
        var first = File.ReadAllBytes(Pack(Config(UpdatesTable.Replace("KEY", Key, StringComparison.Ordinal))).ArchivePath);
        File.SetLastWriteTimeUtc(Path.Combine(_dir.FullName, "build", "Notes.exe"), new DateTime(2021, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var second = File.ReadAllBytes(Pack(Config(UpdatesTable.Replace("KEY", Key, StringComparison.Ordinal))).ArchivePath);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Pack_OutputInsideTheInput_IsNotPacked()
    {
        var result = WindowsZipPackager.Pack(new WindowsZipPackRequest
        {
            Config = Config(),
            Architecture = Architecture.X64,
            OutputDirectory = Path.Combine(_dir.FullName, "build", "dist"),
        });
        Assert.DoesNotContain(Read(result.ArchivePath).Keys, k => k.Contains("dist", StringComparison.Ordinal));
    }

    [Fact]
    public void Pack_WithSigning_SignsTheMainExecutableAndTheUpdater()
    {
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa: false);
        var pfx = Path.Combine(_dir.FullName, "cert.pfx");
        File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pkcs12, "pw"));
        using var credential = SigningCredential.LoadPfx(pfx, "pw");
        var log = new StringWriter();

        var result = Pack(
            Config(UpdatesTable.Replace("KEY", Key, StringComparison.Ordinal)),
            new NsisSigning { Credential = credential, CertificatePath = pfx, ZexoroCommand = ["zexoro"] },
            log);

        Assert.Equal(["Notes.exe"], result.SignedFiles);
        var extracted = Path.Combine(_dir.FullName, "extracted");
        ZipFile.ExtractToDirectory(result.ArchivePath, extracted);
        Assert.True(AuthenticodeSigner.IsSigned(Path.Combine(extracted, "ExampleNotes", "app", "Notes.exe")));
        Assert.True(AuthenticodeSigner.IsSigned(Path.Combine(extracted, "ExampleNotes", "zexoro-updater.exe")));
        Assert.False(AuthenticodeSigner.IsSigned(Path.Combine(extracted, "ExampleNotes", "app", "lib", "helper.dll")));
        Assert.Contains("Signing zexoro-updater.exe", log.ToString(), StringComparison.Ordinal);

        // The inputs are left alone.
        Assert.False(AuthenticodeSigner.IsSigned(Path.Combine(_dir.FullName, "build", "Notes.exe")));
    }

    [Fact]
    public void Pack_WithUpdatesWithoutUpdater_Fails()
    {
        var ex = Assert.Throws<ZipPackException>(() => WindowsZipPackager.Pack(new WindowsZipPackRequest
        {
            Config = Config(UpdatesTable.Replace("KEY", Key, StringComparison.Ordinal)),
            Architecture = Architecture.X64,
            OutputDirectory = Dist,
        }));
        Assert.Contains("no zexoro-updater for win-x64", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Dist));
    }

    [Fact]
    public void Pack_UpdaterWithWrongHash_Fails()
    {
        var ex = Assert.Throws<ZipPackException>(() => WindowsZipPackager.Pack(new WindowsZipPackRequest
        {
            Config = Config(UpdatesTable.Replace("KEY", Key, StringComparison.Ordinal)),
            Architecture = Architecture.X64,
            OutputDirectory = Dist,
            ResolveUpdater = architecture => new UpdaterBinary(architecture, "bad updater", new string('0', 64), () => new MemoryStream(_updater)),
        }));
        Assert.Contains("SHA-256", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(Dist, "ExampleNotes-1.2.3-win-x64.zip")));
    }

    [Fact]
    public void Pack_MissingInput_Fails()
    {
        Directory.Delete(Path.Combine(_dir.FullName, "build"), recursive: true);
        var ex = Assert.Throws<ZipPackException>(() => Pack(Config()));
        Assert.Contains("input.dir", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_UnsupportedArchitecture_Fails()
    {
        var ex = Assert.Throws<ZipPackException>(() => WindowsZipPackager.Pack(new WindowsZipPackRequest
        {
            Config = Config(),
            Architecture = Architecture.Universal,
            OutputDirectory = Dist,
        }));
        Assert.Contains("x86, x64 and arm64", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetOutputFileName_UsesTheArtifactName() =>
        Assert.Equal("ExampleNotes-1.2.3-win-arm64.zip", WindowsZipPackager.GetOutputFileName(Config().App, Architecture.Arm64));
}
