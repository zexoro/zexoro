using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zexoro.Configuration;
using Zexoro.Platforms;

namespace Zexoro.Formats.Linux.Tests;

public sealed class ApkPackagerTests : IDisposable
{
    private static readonly string Key = NewKey();

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-apk-pack-");
    private readonly StringWriter _log = new();
    private readonly List<UpdaterRuntime> _resolved = [];

    public ApkPackagerTests()
    {
        Write("build/notes", TarFiles.Elf(62, "/lib/ld-musl-x86_64.so.1"));
        Write("build/lib/libhelper.so", TarFiles.Elf(62));
        Write("build/data/readme.txt", "hello"u8.ToArray());
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

    private ZexoroConfig Config(string extra = "", string version = "1.2.3", string app = "") => ZexoroConfig.Parse(
        $$"""
        [app]
        id = "com.example.notes"
        name = "Example Notes"
        version = "{{version}}"
        publisher = "Example Ltd."
        icon = "app.ico"
        {{app}}

        [input]
        dir = "build"
        main = "notes"

        [linux]
        formats = ["apk"]
        {{extra}}
        """,
        _dir.FullName);

    private static readonly string UpdatesTable = """

        [updates]
        feed = "https://example.com/updates/{channel}/feed.json"
        download = "https://example.com/dl/{file}"
        keys = ["KEY"]
        """.Replace("KEY", Key, StringComparison.Ordinal);

    private UnixUpdaterBinary Updater(UpdaterRuntime runtime)
    {
        _resolved.Add(runtime);
        var bytes = TarFiles.Elf(runtime.Platform.Architecture == Architecture.Arm64 ? (ushort)183 : (ushort)62);
        return new(runtime, "test updater", Convert.ToHexStringLower(SHA256.HashData(bytes)), () => new MemoryStream(bytes));
    }

    private ApkPackResult Pack(ZexoroConfig config, Architecture? architecture = null, ApkSigner? signer = null, string? output = null) =>
        ApkPackager.Pack(new ApkPackRequest
        {
            Config = config,
            Architecture = architecture ?? Architecture.X64,
            OutputDirectory = output ?? Dist,
            Log = _log,
            ResolveUpdater = Updater,
            Signer = signer,
        });

    private static ApkSigner Signer(RSA rsa, string keyName = "dev@example.com-6123abcd.rsa.pub") =>
        new(keyName, "test key", control => rsa.SignData(control, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

    private static List<ApkFiles.Entry> Entries(byte[] segment) => ApkFiles.Entries(ApkFiles.Decompress(segment));

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "apk's per-file checksums are SHA-1 by definition.")]
    public void Pack_WithUpdates_WritesTheLayoutTheMuslUpdaterAndItsDependencies()
    {
        var result = Pack(Config(
            "maintainer = \"Notes Team <team@example.com>\"\n[linux.apk]\nlicense = \"MIT\"\ndepends = [\"icu-libs>=74\", \"so:libz.so.1\"]" + UpdatesTable,
            app: "url = \"https://example.com/notes\"\ndescription = \"Takes notes\\nQuickly.\""));

        Assert.Equal(Path.Combine(Dist, "notes-1.2.3-r0.x86_64.apk"), result.PackagePath);
        Assert.Equal([UpdaterRuntime.LinuxMuslX64], _resolved);
        var segments = ApkFiles.Segments(File.ReadAllBytes(result.PackagePath));
        Assert.Equal(2, segments.Count);

        var control = Assert.Single(Entries(segments[0]));
        Assert.Equal(".PKGINFO", control.Name);
        Assert.Equal(TarEntryType.RegularFile, control.Type);
        Assert.Equal(result.PackageInfo, Encoding.UTF8.GetString(control.Content));
        Assert.Equal(
            [
                ("pkgname", "notes"),
                ("pkgver", "1.2.3-r0"),
                ("pkgdesc", "Takes notes"),
                ("url", "https://example.com/notes"),
                ("builddate", "318211200"),
                ("packager", "Notes Team <team@example.com>"),
                ("size", ApkPackager.InstalledSize(ItemsOf(segments[1])).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("arch", "x86_64"),
                ("origin", "notes"),
                ("maintainer", "Notes Team <team@example.com>"),
                ("license", "MIT"),
                ("depend", "so:libc.musl-x86_64.so.1"),
                ("depend", "so:libcrypto.so.3"),
                ("depend", "so:libssl.so.3"),
                ("depend", "ca-certificates-bundle"),
                ("depend", "icu-libs>=74"),
                ("depend", "so:libz.so.1"),
                ("datahash", Convert.ToHexStringLower(SHA256.HashData(segments[1]))),
            ],
            ApkFiles.PackageInfo(control.Content));

        var data = Entries(segments[1]);
        Assert.Equal(
            [
                "opt/",
                "opt/notes/",
                "opt/notes/app/",
                "opt/notes/app/data/",
                "opt/notes/app/data/readme.txt",
                "opt/notes/app/lib/",
                "opt/notes/app/lib/libhelper.so",
                "opt/notes/app/notes",
                "opt/notes/zexoro-install.json",
                "opt/notes/zexoro-updater",
                "usr/",
                "usr/bin/",
                "usr/bin/notes",
                "usr/share/",
                "usr/share/applications/",
                "usr/share/applications/com.example.notes.desktop",
                "usr/share/icons/",
                "usr/share/icons/hicolor/",
                "usr/share/icons/hicolor/32x32/",
                "usr/share/icons/hicolor/32x32/apps/",
                "usr/share/icons/hicolor/32x32/apps/com.example.notes.png",
            ],
            data.Select(e => e.Name));
        foreach (var entry in data)
        {
            Assert.Equal((0, 0, "root", "root"), (entry.Uid, entry.Gid, entry.UserName, entry.GroupName));
            Assert.Equal(ReproducibleTar.Timestamp, entry.Modified);
            switch (entry.Type)
            {
                case TarEntryType.Directory:
                    Assert.Equal(TarItem.Executable, entry.Mode);
                    Assert.Empty(entry.Pax);
                    break;
                case TarEntryType.SymbolicLink:
                    Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(entry.LinkName))), entry.Pax[ApkArchive.ChecksumKey]);
                    break;
                default:
                    Assert.Equal(TarEntryType.RegularFile, entry.Type);
                    Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(entry.Content)), entry.Pax[ApkArchive.ChecksumKey]);
                    break;
            }
        }

        var link = data.Single(e => e.Name == "usr/bin/notes");
        Assert.Equal(TarEntryType.SymbolicLink, link.Type);
        Assert.Equal("/opt/notes/app/notes", link.LinkName);
        Assert.Equal(TarItem.Executable, data.Single(e => e.Name == "opt/notes/app/notes").Mode);
        Assert.Equal(TarItem.Executable, data.Single(e => e.Name == "opt/notes/zexoro-updater").Mode);
        Assert.Equal(TarItem.Regular, data.Single(e => e.Name == "opt/notes/app/data/readme.txt").Mode);

        using var record = JsonDocument.Parse(data.Single(e => e.Name == "opt/notes/zexoro-install.json").Content);
        Assert.Equal("apk", record.RootElement.GetProperty("format").GetString());
        Assert.Equal("machine", record.RootElement.GetProperty("scope").GetString());
        Assert.Equal("notify", record.RootElement.GetProperty("strategy").GetString());
        Assert.Equal("x64", record.RootElement.GetProperty("arch").GetString());
        Assert.Contains("Exec=/usr/bin/notes", Encoding.UTF8.GetString(data.Single(e => e.Name.EndsWith(".desktop", StringComparison.Ordinal)).Content), StringComparison.Ordinal);
        Assert.Contains("notice: [linux.apk.signing] is not configured; the apk is unsigned", _log.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("warning", _log.ToString(), StringComparison.Ordinal);
    }

    private static List<TarItem> ItemsOf(byte[] segment) =>
        Entries(segment).Where(e => e.Type == TarEntryType.RegularFile).Select(e => TarItem.FromBytes(e.Name, e.Content, e.Mode)).ToList();

    [Fact]
    public void Pack_Segments_AreCutAsAbuildWritesThem()
    {
        using var rsa = RSA.Create(2048);
        var result = Pack(Config(), signer: Signer(rsa));

        var segments = ApkFiles.Segments(File.ReadAllBytes(result.PackagePath));
        Assert.Equal(3, segments.Count);

        // The signature and control segments end without end-of-archive blocks (abuild-tar --cut),
        // so the three streams form one tar; the data segment ends with them.
        var signature = ApkFiles.Decompress(segments[0]);
        Assert.Equal(512 + 512, signature.Length);
        var control = ApkFiles.Decompress(segments[1]);
        Assert.Equal(512 + ((Encoding.UTF8.GetByteCount(result.PackageInfo) + 511) / 512 * 512), control.Length);
        var data = ApkFiles.Decompress(segments[2]);
        Assert.True(data.AsSpan(data.Length - 1024).IndexOfAnyExcept((byte)0) < 0);

        // ustar headers with magic "ustar\0", version "00", as apk-tools requires.
        Assert.Equal("ustar\000"u8.ToArray(), control.AsSpan(257, 8).ToArray());
        Assert.Equal("ustar\000"u8.ToArray(), signature.AsSpan(257, 8).ToArray());

        var all = ApkFiles.Entries([.. signature, .. control, .. data]);
        Assert.Equal([".SIGN.RSA256.dev@example.com-6123abcd.rsa.pub", ".PKGINFO", "opt/"], all.Take(3).Select(e => e.Name));
    }

    [Fact]
    public void Pack_Signed_SignsTheCompressedControlSegmentWithRsaSha256()
    {
        using var rsa = RSA.Create(2048);
        var result = Pack(Config(), signer: Signer(rsa, "packager-1234.rsa.pub"));

        var segments = ApkFiles.Segments(File.ReadAllBytes(result.PackagePath));
        var sign = Assert.Single(Entries(segments[0]));
        Assert.Equal(".SIGN.RSA256.packager-1234.rsa.pub", sign.Name);
        Assert.Equal(TarItem.Regular, sign.Mode);
        Assert.True(rsa.VerifyData(segments[1], sign.Content, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.DoesNotContain("notice: [linux.apk.signing]", _log.ToString(), StringComparison.Ordinal);
        Assert.Contains("Signing the apk with test key (packager-1234.rsa.pub)", _log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_Twice_WritesTheSameBytes()
    {
        using var rsa = RSA.Create(2048);
        var first = Pack(Config(UpdatesTable), signer: Signer(rsa));
        var second = Pack(Config(UpdatesTable), signer: Signer(rsa), output: Path.Combine(_dir.FullName, "again"));

        Assert.Equal(File.ReadAllBytes(first.PackagePath), File.ReadAllBytes(second.PackagePath));
    }

    [Fact]
    public void Pack_Arm64WithoutUpdates_HasNoUpdaterAndNoUpdaterDependencies()
    {
        var result = Pack(Config(), Architecture.Arm64);

        Assert.Equal(Path.Combine(Dist, "notes-1.2.3-r0.aarch64.apk"), result.PackagePath);
        Assert.Empty(_resolved);
        var segments = ApkFiles.Segments(File.ReadAllBytes(result.PackagePath));
        var info = ApkFiles.PackageInfo(Assert.Single(Entries(segments[0])).Content);
        Assert.Contains(("arch", "aarch64"), info);
        Assert.Contains(("pkgdesc", "Example Notes"), info);
        Assert.DoesNotContain(info, f => f.Key is "depend" or "license" or "url");
        Assert.DoesNotContain(Entries(segments[1]), e => e.Name.EndsWith("zexoro-updater", StringComparison.Ordinal));
        Assert.Contains("notice: [linux.apk] license is not set", _log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_Arm64WithUpdates_DependsOnTheAarch64Musl()
    {
        var result = Pack(Config(UpdatesTable), Architecture.Arm64);

        Assert.Equal([UpdaterRuntime.LinuxMuslArm64], _resolved);
        var info = ApkFiles.PackageInfo(Assert.Single(Entries(ApkFiles.Segments(File.ReadAllBytes(result.PackagePath))[0])).Content);
        Assert.Contains(("depend", "so:libc.musl-aarch64.so.1"), info);
    }

    [Fact]
    public void Pack_Prerelease_MapsTheVersion()
    {
        var result = Pack(Config(version: "2.0.0-rc.3+build.7"));

        Assert.Equal(Path.Combine(Dist, "notes-2.0.0_rc3-r0.x86_64.apk"), result.PackagePath);
        Assert.Contains("pkgver = 2.0.0_rc3-r0\n", result.PackageInfo, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_UnsupportedPrerelease_Fails()
    {
        var ex = Assert.Throws<LinuxPackException>(() => Pack(Config(version: "1.0.0-preview.1")));

        Assert.StartsWith("app.version: the pre-release 'preview.1' of version 1.0.0-preview.1 has no apk version", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Dist));
    }

    [Fact]
    public void Pack_LongNamesAndTargets_UsePaxPathAndLinkpath()
    {
        var deep = "build/" + string.Join('/', Enumerable.Repeat("directory-name", 8)) + "/file.txt";
        Write(deep, "deep"u8.ToArray());
        var target = string.Join('/', Enumerable.Repeat("..", 1)) + "/" + string.Join('/', Enumerable.Repeat("directory-name", 8)) + "/file.txt";
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(_dir.FullName, "build/data/link-to-deep"), target);
        }

        var result = Pack(Config());

        var data = Entries(ApkFiles.Segments(File.ReadAllBytes(result.PackagePath))[1]);
        var file = data.Single(e => e.Name.EndsWith("/file.txt", StringComparison.Ordinal));
        Assert.True(Encoding.UTF8.GetByteCount(file.Name) > 100);
        Assert.Equal(file.Name, file.Pax["path"]);
        Assert.Equal("deep"u8.ToArray(), file.Content);
        if (!OperatingSystem.IsWindows())
        {
            var link = data.Single(e => e.Name == "opt/notes/app/data/link-to-deep");
            Assert.True(target.Length > 100);
            Assert.Equal(target, link.LinkName);
            Assert.Equal(target, link.Pax["linkpath"]);
        }
    }

    [Fact]
    public void Pack_GlibcMainExecutable_Warns()
    {
        Write("build/notes", TarFiles.Elf(62, "/lib64/ld-linux-x86-64.so.2"));

        Pack(Config());

        Assert.Contains("warning: input.main is linked against glibc (/lib64/ld-linux-x86-64.so.2)", _log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PaxData_CountsTheLengthFieldItself()
    {
        var data = Encoding.UTF8.GetString(ApkArchive.PaxData([("k", "v"), ("path", new string('a', 90)), ("x", new string('b', 993))]));

        var records = data.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("6 k=v", records[0]);
        Assert.All(records, r => Assert.Equal(int.Parse(r.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture), Encoding.UTF8.GetByteCount(r) + 1));
    }
}
