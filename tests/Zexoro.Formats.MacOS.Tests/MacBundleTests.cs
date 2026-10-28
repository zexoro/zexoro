using System.Security.Cryptography;
using System.Text.Json;
using Zexoro.Configuration;
using Zexoro.Formats.Linux;
using Zexoro.Platforms;

namespace Zexoro.Formats.MacOS.Tests;

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MacBundleTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-bundle-");

    public MacBundleTests()
    {
        Write("build/Notes", FatMachOTests.Thin(FatMachO.CpuTypeArm64, 0));
        Write("build/libnative.dylib", FatMachOTests.Thin(FatMachO.CpuTypeArm64, 0));
        Write("build/Notes.dll", "MZ managed"u8.ToArray());
        Write("build/data/readme.txt", "hello"u8.ToArray());
        Write("build/tool.sh", "#!/bin/sh\necho hi\n"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_dir.FullName, "app.png"), TestImages.Png(256));
    }

    public void Dispose() => _dir.Delete(recursive: true);

    private static void SkipOnWindows() =>
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Bundles keep Unix modes and symbolic links, and pack only on macOS.");

    private string Staging => Directory.CreateDirectory(Path.Combine(_dir.FullName, "staging")).FullName;

    internal static string NewKey()
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

    private ZexoroConfig Config(string extra = "", string dir = "build", string main = "Notes") => ZexoroConfig.Parse(
        $$"""
        [app]
        id = "com.example.notes"
        name = "Example Notes"
        version = "1.2.3-beta.1"
        publisher = "Example Ltd."
        icon = "app.png"

        [input]
        dir = "{{dir}}"
        main = "{{main}}"

        [macos]
        minimum_version = "12.0"
        category = "public.app-category.productivity"
        {{extra}}
        """,
        _dir.FullName);

    private MacBundleResult Create(ZexoroConfig config, byte[]? updater = null) => MacBundle.Create(new MacBundleRequest
    {
        Config = config,
        Architecture = Architecture.Arm64,
        StagingDirectory = Staging,
        Updater = updater,
        Updates = updater is null ? null : new Formats.Nsis.NsisUpdates("stable", new Uri("https://example.com/feed.json"), [NewKey()]),
        LoadIcon = () => MacZipPackager.LoadIcon(config),
    });

    [Fact]
    public void Create_FlatInput_KeepsCodeInMacOSAndLinksDataFromResources()
    {
        SkipOnWindows();
        var bundle = Create(Config());

        Assert.Equal(Path.Combine(_dir.FullName, "staging", "Example Notes.app"), bundle.Path);
        Assert.Equal("Contents/MacOS/Notes", bundle.MainExecutable);
        Assert.True(bundle.Generated);
        string At(string relative) => Path.Combine(bundle.Path, relative);
        Assert.Null(new FileInfo(At("Contents/MacOS/Notes")).LinkTarget);
        Assert.Equal((UnixFileMode)0x1ED, File.GetUnixFileMode(At("Contents/MacOS/Notes")));
        Assert.Null(new FileInfo(At("Contents/MacOS/libnative.dylib")).LinkTarget);
        Assert.Equal("../../MacOS/libnative.dylib", new FileInfo(At("Contents/Resources/app/libnative.dylib")).LinkTarget);
        Assert.Equal("../../MacOS/Notes", new FileInfo(At("Contents/Resources/app/Notes")).LinkTarget);
        Assert.True(Directory.Exists(At("Contents/Resources/app/data")));
        Assert.Equal("../Resources/app/Notes.dll", new FileInfo(At("Contents/MacOS/Notes.dll")).LinkTarget);
        Assert.Equal("../../Resources/app/data/readme.txt", new FileInfo(At("Contents/MacOS/data/readme.txt")).LinkTarget);
        Assert.Equal("hello", File.ReadAllText(At("Contents/MacOS/data/readme.txt")));
        Assert.Equal((UnixFileMode)0x1A4, File.GetUnixFileMode(At("Contents/Resources/app/Notes.dll")));
        Assert.Equal((UnixFileMode)0x1ED, File.GetUnixFileMode(At("Contents/Resources/app/tool.sh")));
        Assert.True(Icns.IsIcns(File.ReadAllBytes(At("Contents/Resources/AppIcon.icns"))));
        Assert.Equal("APPL????", File.ReadAllText(At("Contents/PkgInfo")));
        Assert.False(File.Exists(At("Contents/MacOS/zexoro-updater")));

        var plist = PropertyList.Read(File.ReadAllBytes(At("Contents/Info.plist")));
        Assert.Equal("com.example.notes", plist["CFBundleIdentifier"]);
        Assert.Equal("Notes", plist["CFBundleExecutable"]);
        Assert.Equal("1.2.3", plist["CFBundleShortVersionString"]);
        Assert.Equal("1.2.3", plist["CFBundleVersion"]);
        Assert.Equal("Example Notes", plist["CFBundleName"]);
        Assert.Equal("AppIcon", plist["CFBundleIconFile"]);
        Assert.Equal("APPL", plist["CFBundlePackageType"]);
        Assert.Equal("12.0", plist["LSMinimumSystemVersion"]);
        Assert.Equal("public.app-category.productivity", plist["LSApplicationCategoryType"]);

        using var record = JsonDocument.Parse(File.ReadAllBytes(At("Contents/Resources/zexoro-install.json")));
        var root = record.RootElement;
        Assert.Equal("com.example.notes", root.GetProperty("app").GetString());
        Assert.Equal("1.2.3-beta.1", root.GetProperty("version").GetString());
        Assert.Equal("macos", root.GetProperty("os").GetString());
        Assert.Equal("arm64", root.GetProperty("arch").GetString());
        Assert.Equal("app", root.GetProperty("format").GetString());
        Assert.Equal("user", root.GetProperty("scope").GetString());
        Assert.Equal("bundle", root.GetProperty("strategy").GetString());
        Assert.False(root.TryGetProperty("feed", out _));
    }

    [Fact]
    public void Create_WithUpdater_AddsItAndAFullRecord()
    {
        SkipOnWindows();
        var updater = FatMachOTests.Thin(FatMachO.CpuTypeArm64, 0);

        var bundle = Create(Config(), updater);

        var path = Path.Combine(bundle.Path, "Contents", "MacOS", "zexoro-updater");
        Assert.Equal(updater, File.ReadAllBytes(path));
        Assert.Equal((UnixFileMode)0x1ED, File.GetUnixFileMode(path));
        using var record = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(bundle.Path, MacBundle.RecordPath)));
        Assert.Equal("https://example.com/feed.json", record.RootElement.GetProperty("feed").GetString());
        Assert.Equal("Contents/MacOS/Notes", record.RootElement.GetProperty("main").GetString());
    }

    [Fact]
    public void Create_BundleId_OverridesAppId()
    {
        SkipOnWindows();
        var bundle = Create(Config("bundle_id = \"com.example.Notes-App\""));

        Assert.Equal("com.example.Notes-App", PropertyList.Read(File.ReadAllBytes(Path.Combine(bundle.Path, "Contents", "Info.plist")))["CFBundleIdentifier"]);
    }

    [Fact]
    public void Create_KeepsRelativeLinksAndRefusesOthers()
    {
        SkipOnWindows();
        File.CreateSymbolicLink(Path.Combine(_dir.FullName, "build", "readme.txt"), "data/readme.txt");
        var bundle = Create(Config());
        Assert.Equal("data/readme.txt", new FileInfo(Path.Combine(bundle.Path, "Contents", "MacOS", "readme.txt")).LinkTarget);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(bundle.Path, "Contents", "Resources", "app", "readme.txt")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(bundle.Path, "Contents", "MacOS", "readme.txt")));

        File.CreateSymbolicLink(Path.Combine(_dir.FullName, "build", "escape"), "../app.png");
        Directory.Delete(Path.Combine(_dir.FullName, "staging"), recursive: true);
        var error = Assert.Throws<MacPackException>(() => Create(Config()));
        Assert.Contains("'escape' points to '../app.png'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_RefusesNestedBundlesAndAMainThatIsNotMachO()
    {
        SkipOnWindows();
        Directory.CreateDirectory(Path.Combine(_dir.FullName, "build", "Helper.app"));
        Assert.Contains("'Helper.app' is a nested bundle", Assert.Throws<MacPackException>(() => Create(Config())).Message, StringComparison.Ordinal);

        Directory.Delete(Path.Combine(_dir.FullName, "build", "Helper.app"));
        Directory.Delete(Path.Combine(_dir.FullName, "staging"), recursive: true);
        Assert.Contains("is not a Mach-O executable", Assert.Throws<MacPackException>(() => Create(Config(main: "tool.sh"))).Message, StringComparison.Ordinal);
        Assert.Contains("is in a subdirectory", Assert.Throws<MacPackException>(() => Create(Config(main: "data/readme.txt"))).Message, StringComparison.Ordinal);
    }

    private void WriteApp(string id, string version, string executable = "Notes", bool binary = false)
    {
        Write("Notes.app/Contents/MacOS/" + executable, FatMachOTests.Thin(FatMachO.CpuTypeArm64, 0));
        Write("Notes.app/Contents/Resources/en.lproj/Main.strings", "x"u8.ToArray());
        Directory.CreateDirectory(Path.Combine(_dir.FullName, "Notes.app/Contents/Frameworks/Lib.framework/Versions/A"));
        Write("Notes.app/Contents/Frameworks/Lib.framework/Versions/A/Lib", FatMachOTests.Thin(FatMachO.CpuTypeArm64, 0));
        File.CreateSymbolicLink(Path.Combine(_dir.FullName, "Notes.app/Contents/Frameworks/Lib.framework/Versions/Current"), "A");
        File.CreateSymbolicLink(Path.Combine(_dir.FullName, "Notes.app/Contents/Frameworks/Lib.framework/Lib"), "Versions/Current/Lib");
        var plist = new Dictionary<string, object>
        {
            ["CFBundleIdentifier"] = id,
            ["CFBundleShortVersionString"] = version,
            ["CFBundleExecutable"] = executable,
            ["CFBundleName"] = "Notes",
        };
        Write("Notes.app/Contents/Info.plist", binary ? Convert.FromBase64String(BinaryInfoPlist) : PropertyList.ToXml(plist));
    }

    /// <summary>plutil -convert binary1 of { CFBundleExecutable = Notes; CFBundleIdentifier = com.example.notes; CFBundleShortVersionString = 1.2.3 }.</summary>
    private const string BinaryInfoPlist =
        "YnBsaXN0MDDTAQIDBAUGXxASQ0ZCdW5kbGVJZGVudGlmaWVyXxAaQ0ZCdW5kbGVTaG9ydFZlcnNpb25TdHJpbmdfEBJDRkJ1bmRsZUV4ZWN1dGFibGVfEBFjb20uZXhhbXBsZS5ub3Rlc1UxLjIuM1VOb3RlcwgPJEFWanAAAAAAAAABAQAAAAAAAAAHAAAAAAAAAAAAAAAAAAAAdg==";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Create_ExistingApp_IsCopiedWithItsLinks(bool binary)
    {
        SkipOnWindows();
        WriteApp("com.example.notes", "1.2.3", binary: binary);

        var bundle = Create(Config(dir: "Notes.app", main: "Contents/MacOS/Notes"), FatMachOTests.Thin(FatMachO.CpuTypeArm64, 0));

        Assert.False(bundle.Generated);
        Assert.Equal(Path.Combine(_dir.FullName, "staging", "Example Notes.app"), bundle.Path);
        Assert.Equal("A", new FileInfo(Path.Combine(bundle.Path, "Contents/Frameworks/Lib.framework/Versions/Current")).LinkTarget);
        Assert.Equal("Versions/Current/Lib", new FileInfo(Path.Combine(bundle.Path, "Contents/Frameworks/Lib.framework/Lib")).LinkTarget);
        Assert.True(File.Exists(Path.Combine(bundle.Path, "Contents/Resources/en.lproj/Main.strings")));
        Assert.True(File.Exists(Path.Combine(bundle.Path, "Contents/MacOS/zexoro-updater")));
        Assert.True(File.Exists(Path.Combine(bundle.Path, MacBundle.RecordPath)));
        Assert.False(File.Exists(Path.Combine(bundle.Path, "Contents/Resources/AppIcon.icns")));
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(_dir.FullName, "Notes.app/Contents/Info.plist")),
            File.ReadAllBytes(Path.Combine(bundle.Path, "Contents/Info.plist")));
    }

    [Theory]
    [InlineData("com.example.other", "1.2.3", "Notes", "CFBundleIdentifier is 'com.example.other', but app.id is 'com.example.notes'")]
    [InlineData("com.example.notes", "1.2.4", "Notes", "CFBundleShortVersionString is '1.2.4', but app.version 1.2.3-beta.1 needs '1.2.3'")]
    [InlineData("com.example.notes", "1.2.3", "Other", "the bundle's main executable is 'Contents/MacOS/Other'")]
    public void Create_ExistingApp_MustMatchTheConfiguration(string id, string version, string executable, string message)
    {
        SkipOnWindows();
        WriteApp(id, version, executable);

        var error = Assert.Throws<MacPackException>(() => Create(Config(dir: "Notes.app", main: "Contents/MacOS/" + (executable == "Other" ? "Notes" : executable))));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadIcon_UsesIcnsAsItIsAndConvertsPng()
    {
        SkipOnWindows();
        var icns = Icns.Create([PngIcon.FromBytes(TestImages.Png(64))]);
        File.WriteAllBytes(Path.Combine(_dir.FullName, "app.icns"), icns);

        Assert.Equal(icns, MacZipPackager.LoadIcon(Config("icon = \"app.icns\"")));
        Assert.True(Icns.IsIcns(MacZipPackager.LoadIcon(Config())));

        File.WriteAllBytes(Path.Combine(_dir.FullName, "odd.png"), TestImages.Png(100));
        var error = Assert.Throws<MacPackException>(() => MacZipPackager.LoadIcon(Config("icon = \"odd.png\"")));
        Assert.Contains("a 100x100 image", error.Message, StringComparison.Ordinal);
    }
}
