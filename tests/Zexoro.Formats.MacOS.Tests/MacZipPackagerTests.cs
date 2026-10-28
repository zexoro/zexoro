using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Zexoro.Configuration;
using Zexoro.Formats.Linux;
using Zexoro.Platforms;

namespace Zexoro.Formats.MacOS.Tests;

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MacZipPackagerTests : IDisposable
{
    private static readonly string Key = MacBundleTests.NewKey();

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-maczip-");
    private readonly StringWriter _log = new();

    public void Dispose()
    {
        _log.Dispose();
        _dir.Delete(recursive: true);
    }

    private string Dist => Path.Combine(_dir.FullName, "dist");

    private void Arrange(byte[]? main = null)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Bundles keep Unix modes and symbolic links, and pack only on macOS.");
        Write("build/Notes", main ?? FatMachOTests.Thin(FatMachO.CpuTypeArm64, 0));
        if (main is not null)
        {
            File.SetUnixFileMode(Path.Combine(_dir.FullName, "build/Notes"), (UnixFileMode)0x1ED);
        }

        Write("build/Notes.dll", "MZ"u8.ToArray());
        Write("build/data/readme.txt", "hello"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_dir.FullName, "app.png"), TestImages.Png(64));
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
        icon = "app.png"

        [input]
        dir = "build"
        main = "Notes"

        {{extra}}
        """,
        _dir.FullName);

    private const string Updates = """
        [updates]
        feed = "https://example.com/{channel}/feed.json"
        download = "https://example.com/{file}"
        keys = ["KEY"]
        """;

    private static UnixUpdaterBinary Updater(UpdaterRuntime runtime)
    {
        var bytes = FatMachOTests.Thin(runtime == UpdaterRuntime.OsxX64 ? FatMachO.CpuTypeX64 : FatMachO.CpuTypeArm64, runtime == UpdaterRuntime.OsxX64 ? 3u : 0u);
        return new UnixUpdaterBinary(runtime, $"test {runtime.Rid}", Convert.ToHexStringLower(SHA256.HashData(bytes)), () => new MemoryStream(bytes));
    }

    private MacZipPackResult Pack(ZexoroConfig config, IMacToolRunner tools, Architecture? architecture = null, MacSigningIdentity? signing = null, MacNotarizationKey? notarization = null) =>
        MacZipPackager.Pack(new MacZipPackRequest
        {
            Config = config,
            Architecture = architecture ?? Architecture.Arm64,
            OutputDirectory = Dist,
            Tools = tools,
            Signing = signing,
            Notarization = notarization,
            ResolveUpdater = Updater,
            Log = _log,
        });

    [Fact]
    public void Pack_WritesTheBundleWithModesAndLinks()
    {
        Arrange();
        var tools = new FakeTools();

        var result = Pack(Config(), tools);

        Assert.Equal(Path.Combine(Dist, "ExampleNotes-1.2.3-osx-arm64.zip"), result.ZipPath);
        Assert.False(result.Notarized);
        Assert.Contains("signed ad hoc", _log.ToString(), StringComparison.Ordinal);
        Assert.Contains(tools.Calls, c => c.Arguments.SequenceEqual(["--force", "--sign", "-", Path.Combine(Path.GetDirectoryName(c.Arguments[^1])!, "Example Notes.app")]));
        Assert.Equal(["ExampleNotes-1.2.3-osx-arm64.zip"], Directory.GetFileSystemEntries(Dist).Select(Path.GetFileName));

        using var zip = ZipFile.OpenRead(result.ZipPath);
        var entries = zip.Entries.ToDictionary(e => e.FullName);
        Assert.Equal(entries.Keys.Order(StringComparer.Ordinal), entries.Keys);
        int Mode(string name) => (entries[name].ExternalAttributes >> 16) & 0xFFFF;
        Assert.Equal(0x41ED, Mode("Example Notes.app/"));
        Assert.Equal(0x81ED, Mode("Example Notes.app/Contents/MacOS/Notes"));
        Assert.Equal(0x81A4, Mode("Example Notes.app/Contents/Info.plist"));
        Assert.Equal(0xA1ED, Mode("Example Notes.app/Contents/MacOS/Notes.dll"));
        using (var reader = new StreamReader(entries["Example Notes.app/Contents/MacOS/Notes.dll"].Open()))
        {
            Assert.Equal("../Resources/app/Notes.dll", reader.ReadToEnd());
        }

        Assert.Contains("Example Notes.app/Contents/Resources/zexoro-install.json", entries.Keys);
        Assert.DoesNotContain("Example Notes.app/Contents/MacOS/zexoro-updater", entries.Keys);
        Assert.All(entries.Values, e => Assert.Equal(new DateTime(1980, 2, 1), e.LastWriteTime.DateTime));
    }

    [Fact]
    public void Pack_UpdatesWithoutSigning_IsRefusedBeforeAnythingIsWritten()
    {
        Arrange();
        var tools = new FakeTools();

        var error = Assert.Throws<MacPackException>(() => Pack(Config(Updates.Replace("KEY", Key, StringComparison.Ordinal)), tools));

        Assert.Contains("[updates] needs [macos.signing]", error.Message, StringComparison.Ordinal);
        Assert.Empty(tools.Calls);
        Assert.False(Directory.Exists(Dist));
    }

    [Fact]
    public void Pack_Universal_AddsAUniversalUpdater()
    {
        Arrange();
        var tools = new FakeTools();

        var result = Pack(Config(Updates.Replace("KEY", Key, StringComparison.Ordinal)), tools, Architecture.Universal, new MacSigningIdentity("Example", null, null));

        Assert.EndsWith("ExampleNotes-1.2.3-osx-universal.zip", result.ZipPath, StringComparison.Ordinal);
        using var zip = ZipFile.OpenRead(result.ZipPath);
        using var updater = new MemoryStream();
        using (var stream = zip.GetEntry("Example Notes.app/Contents/MacOS/zexoro-updater")!.Open())
        {
            stream.CopyTo(updater);
        }

        Assert.Equal("osx-universal", ExecutableInspector.Inspect(new MemoryStream(updater.ToArray())).Target?.Rid);
        Assert.Contains("not notarized", _log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_Notarization_ZipsTheStapledBundleAgain()
    {
        Arrange();
        File.WriteAllText(Path.Combine(_dir.FullName, "AuthKey_ABC.p8"), "key");
        var tools = new FakeTools((_, arguments) =>
        {
            if (arguments[0] == "notarytool")
            {
                return new ToolResult(0, """{"id":"1","status":"Accepted"}""", string.Empty);
            }

            if (arguments[0] == "stapler")
            {
                File.WriteAllText(Path.Combine(arguments[^1], "Contents", "CodeResources"), "ticket");
            }

            return new ToolResult(0, string.Empty, string.Empty);
        });

        var result = Pack(Config(), tools, signing: new MacSigningIdentity("Example", null, null),
            notarization: new MacNotarizationKey(Path.Combine(_dir.FullName, "AuthKey_ABC.p8"), "ABC", "issuer"));

        Assert.True(result.Notarized);
        using var zip = ZipFile.OpenRead(result.ZipPath);
        Assert.NotNull(zip.GetEntry("Example Notes.app/Contents/CodeResources"));
        Assert.Equal("--verify", tools.Calls[^1].Arguments[0]);
    }

    /// <summary>
    /// The real codesign (ad hoc) signs the generated bundle, the bundle survives the zip, and
    /// <c>ditto -x -k</c> (what Finder's Archive Utility uses) restores links so the signature verifies.
    /// </summary>
    [Fact]
    public void Pack_SignatureSurvivesTheZipAndDitto()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "codesign and ditto exist on macOS only.");
        var apphost = Path.Combine(AppContext.BaseDirectory, "Zexoro.Formats.MacOS.Tests");
        Assert.SkipUnless(File.Exists(apphost), "The test runner has no apphost to use as a Mach-O main executable.");
        Arrange(File.ReadAllBytes(apphost));

        var result = Pack(Config(), ProcessToolRunner.Instance, ExecutableInspector.Inspect(apphost).Target!.Value.Architecture);

        var extracted = Directory.CreateDirectory(Path.Combine(_dir.FullName, "extracted")).FullName;
        Run("/usr/bin/ditto", "-x", "-k", result.ZipPath, extracted);
        var bundle = Path.Combine(extracted, "Example Notes.app");
        Assert.Equal("../Resources/app/Notes.dll", new FileInfo(Path.Combine(bundle, "Contents/MacOS/Notes.dll")).LinkTarget);
        Run("/usr/bin/codesign", "--verify", "--deep", "--strict", "--verbose=2", bundle);
    }

    private static void Run(string file, params string[] arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardError = true, RedirectStandardOutput = true })!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{file} {string.Join(' ', arguments)}: {output}{error.Result}");
    }
}
