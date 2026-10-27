using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zexoro.Configuration;
using Zexoro.Platforms;
using Zexoro.Versioning;

namespace Zexoro.Formats.Linux.Tests;

public sealed class RpmPackagerTests : IDisposable
{
    private static readonly string Key = NewKey();

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-rpm-pack-");
    private readonly StringWriter _log = new();

    public RpmPackagerTests()
    {
        Write("build/notes", TarFiles.Elf(62));
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
        formats = ["rpm"]
        {{extra}}
        """,
        _dir.FullName);

    private static readonly string UpdatesTable = """

        [updates]
        feed = "https://example.com/updates/{channel}/feed.json"
        download = "https://example.com/dl/{file}"
        keys = ["KEY"]
        """.Replace("KEY", Key, StringComparison.Ordinal);

    private static UnixUpdaterBinary Updater(UpdaterRuntime runtime, byte[] bytes) =>
        new(runtime, "test updater", Convert.ToHexStringLower(SHA256.HashData(bytes)), () => new MemoryStream(bytes));

    private RpmPackResult Pack(ZexoroConfig config, Architecture? architecture = null, RpmHeaderSigner? signer = null) =>
        RpmPackager.Pack(new RpmPackRequest
        {
            Config = config,
            Architecture = architecture ?? Architecture.X64,
            OutputDirectory = Dist,
            Log = _log,
            ResolveUpdater = runtime => Updater(runtime, TarFiles.Elf(runtime.Platform.Architecture == Architecture.Arm64 ? (ushort)183 : (ushort)62)),
            Signer = signer,
        });

    [Fact]
    public void Pack_WithUpdates_WritesTheLayoutAndTheUpdatersRequirements()
    {
        var result = Pack(Config("maintainer = \"Notes Team <team@example.com>\"\n[linux.rpm]\nlicense = \"MIT\"\nrequires = [\"coreutils >= 8.0\", \"bash\"]" + UpdatesTable, app: "url = \"https://example.com/notes\"\ndescription = \"Takes notes\\nQuickly.\""));

        Assert.Equal(Path.Combine(Dist, "notes-1.2.3-1.x86_64.rpm"), result.PackagePath);
        var rpm = RpmFiles.Read(result.PackagePath);
        var main = rpm.Main;
        Assert.Equal("notes", main.String(RpmTag.Name));
        Assert.Equal("1.2.3", main.String(RpmTag.Version));
        Assert.Equal("1", main.String(RpmTag.Release));
        Assert.Equal("x86_64", main.String(RpmTag.Arch));
        Assert.Equal("linux", main.String(RpmTag.Os));
        Assert.Equal("Takes notes", main.Strings(RpmTag.Summary)[0]);
        Assert.Equal("Takes notes\nQuickly.", main.Strings(RpmTag.Description)[0]);
        Assert.Equal("MIT", main.String(RpmTag.License));
        Assert.Equal("Notes Team <team@example.com>", main.String(RpmTag.Packager));
        Assert.Equal("Example Ltd.", main.String(RpmTag.Vendor));
        Assert.Equal("https://example.com/notes", main.String(RpmTag.Url));
        Assert.Equal("notes-1.2.3-1.src.rpm", main.String(RpmTag.SourceRpm));
        Assert.Equal(["cpio", "gzip"], new[] { main.String(RpmTag.PayloadFormat), main.String(RpmTag.PayloadCompressor) });

        var requires = main.Strings(RpmTag.RequireName).Zip(main.Strings(RpmTag.RequireVersion), main.Numbers(RpmTag.RequireFlags)).ToList();
        Assert.Equal(
            [
                ("(libssl.so.3()(64bit) or libssl.so.1.1()(64bit))", string.Empty, 0L),
                ("bash", string.Empty, 0L),
                ("coreutils", "8.0", 12L),
                ("libc.so.6(GLIBC_2.28)(64bit)", string.Empty, 0L),
                ("rpmlib(CompressedFileNames)", "3.0.4-1", 0x100000AL),
                ("rpmlib(FileDigests)", "4.6.0-1", 0x100000AL),
                ("rpmlib(PayloadFilesHavePrefix)", "4.0-1", 0x100000AL),
                ("rpmlib(RichDependencies)", "4.12.0-1", 0x100000AL),
            ],
            requires);
        Assert.Equal(["ca-certificates"], main.Strings(RpmTag.RecommendName));
        Assert.Equal(["notes", "notes(x86-64)"], main.Strings(RpmTag.ProvideName));
        Assert.Equal(["1.2.3-1", "1.2.3-1"], main.Strings(RpmTag.ProvideVersion));
        Assert.Equal([8L, 8L], main.Numbers(RpmTag.ProvideFlags));

        var paths = FilePaths(main);
        Assert.Equal(
            [
                "/opt/notes",
                "/opt/notes/app",
                "/opt/notes/app/data",
                "/opt/notes/app/data/readme.txt",
                "/opt/notes/app/lib",
                "/opt/notes/app/lib/libhelper.so",
                "/opt/notes/app/notes",
                "/opt/notes/zexoro-install.json",
                "/opt/notes/zexoro-updater",
                "/usr/bin/notes",
                "/usr/share/applications/com.example.notes.desktop",
                "/usr/share/icons/hicolor/32x32/apps/com.example.notes.png",
            ],
            paths);
        Assert.Equal(paths.Select(p => "." + p), rpm.Entries.Select(e => e.Name));

        var record = rpm.Entries.Single(e => e.Name == "./opt/notes/zexoro-install.json").Data;
        using var json = JsonDocument.Parse(record);
        Assert.Equal("rpm", json.RootElement.GetProperty("format").GetString());
        Assert.Equal("machine", json.RootElement.GetProperty("scope").GetString());
        Assert.Equal("notify", json.RootElement.GetProperty("strategy").GetString());
        Assert.Contains("notice: [linux.rpm.signing] is not configured", _log.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("license is not set", _log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_FileMetadata_MatchesThePayload()
    {
        var rpm = RpmFiles.Read(Pack(Config(UpdatesTable)).PackagePath);
        var main = rpm.Main;
        var paths = FilePaths(main);
        var modes = main.Numbers(RpmTag.FileModes);
        var sizes = main.Numbers(RpmTag.FileSizes);
        var digests = main.Strings(RpmTag.FileDigests);
        var links = main.Strings(RpmTag.FileLinkTos);
        Assert.Equal(8, main.Numbers(RpmTag.FileDigestAlgo)[0]);
        Assert.All(main.Strings(RpmTag.FileUserName), u => Assert.Equal("root", u));
        Assert.All(main.Strings(RpmTag.FileGroupName), g => Assert.Equal("root", g));
        Assert.Equal(Enumerable.Range(1, paths.Count).Select(i => (long)i), main.Numbers(RpmTag.FileInodes));
        Assert.All(main.Numbers(RpmTag.FileMtimes), t => Assert.Equal(ReproducibleTar.Timestamp.ToUnixTimeSeconds(), t));
        for (var i = 0; i < paths.Count; i++)
        {
            var entry = rpm.Entries[i];
            Assert.Equal(modes[i], entry.Mode);
            Assert.Equal(i + 1, entry.Inode);
            Assert.Equal(0, entry.Uid);
            switch (modes[i] & 0xF000)
            {
                case 0x8000:
                    Assert.Equal(sizes[i], entry.Data.Length);
                    Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(entry.Data)), digests[i]);
                    Assert.Equal(string.Empty, links[i]);
                    break;
                case 0xA000:
                    Assert.Equal(links[i], Encoding.UTF8.GetString(entry.Data));
                    Assert.Equal(string.Empty, digests[i]);
                    break;
                default:
                    Assert.Equal(0x4000, modes[i] & 0xF000);
                    Assert.Empty(entry.Data);
                    break;
            }
        }

        Assert.Equal(0x41ED, modes[paths.IndexOf("/opt/notes")]);
        Assert.Equal(0x81ED, modes[paths.IndexOf("/opt/notes/app/notes")]);
        Assert.Equal(0x81ED, modes[paths.IndexOf("/opt/notes/zexoro-updater")]);
        Assert.Equal(0x81A4, modes[paths.IndexOf("/opt/notes/app/data/readme.txt")]);
        Assert.Equal(0xA1FF, modes[paths.IndexOf("/usr/bin/notes")]);
        Assert.Equal("/opt/notes/app/notes", links[paths.IndexOf("/usr/bin/notes")]);
        Assert.Equal(sizes.Sum(), main.Numbers(RpmTag.Size)[0]);
    }

    [Fact]
    public void Pack_Digests_CoverHeaderAndPayload()
    {
        var rpm = RpmFiles.Read(Pack(Config()).PackagePath);
        var signature = rpm.Signature;

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(rpm.MainBytes)), signature.String(RpmTag.Sha256Header));
#pragma warning disable CA5350, CA5351 // rpm's legacy digests under test
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(rpm.MainBytes)), signature.String(RpmTag.Sha1Header));
        Assert.Equal(MD5.HashData([.. rpm.MainBytes, .. rpm.Payload]), signature.Binary(RpmTag.SignatureMd5));
#pragma warning restore CA5350, CA5351
        Assert.Equal(rpm.MainBytes.Length + rpm.Payload.Length, signature.Numbers(RpmTag.SignatureSize)[0]);
        Assert.Equal(rpm.Cpio.Length, signature.Numbers(RpmTag.SignaturePayloadSize)[0]);
        Assert.False(signature.Tags.ContainsKey(RpmTag.RsaHeader));
        Assert.Equal([Convert.ToHexStringLower(SHA256.HashData(rpm.Payload))], rpm.Main.Strings(RpmTag.PayloadDigest));
        Assert.Equal([Convert.ToHexStringLower(SHA256.HashData(rpm.Cpio))], rpm.Main.Strings(RpmTag.PayloadDigestAlt));
        Assert.Equal(8, rpm.Main.Numbers(RpmTag.PayloadDigestAlgo)[0]);
    }

    [Fact]
    public void Pack_WithoutUpdates_HasNoUpdaterAndOnlyTheConfiguredRequirements()
    {
        var rpm = RpmFiles.Read(Pack(Config("[linux.rpm]\nrequires = [\"(libicu or libicu74)\"]")).PackagePath);

        Assert.DoesNotContain("/opt/notes/zexoro-updater", FilePaths(rpm.Main));
        Assert.Equal(
            ["(libicu or libicu74)", "rpmlib(CompressedFileNames)", "rpmlib(FileDigests)", "rpmlib(PayloadFilesHavePrefix)", "rpmlib(RichDependencies)"],
            rpm.Main.Strings(RpmTag.RequireName));
        Assert.False(rpm.Main.Tags.ContainsKey(RpmTag.RecommendName));
        Assert.False(rpm.Main.Tags.ContainsKey(RpmTag.License));
        Assert.Contains("notice: [linux.rpm] license is not set", _log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_Signer_SignsTheMainHeader()
    {
        byte[]? signed = null;
        var signer = new RpmHeaderSigner("test key", header =>
        {
            signed = header;
            return [0x89, 0, 1, 0x42];
        });

        var rpm = RpmFiles.Read(Pack(Config(), signer: signer).PackagePath);

        Assert.Equal(rpm.MainBytes, signed);
        Assert.Equal([0x89, 0, 1, 0x42], rpm.Signature.Binary(RpmTag.RsaHeader));
        Assert.Contains("Signing the rpm header with test key", _log.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("is unsigned", _log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_Arm64_UsesAarch64Names()
    {
        var result = Pack(Config(), Architecture.Arm64);

        Assert.Equal(Path.Combine(Dist, "notes-1.2.3-1.aarch64.rpm"), result.PackagePath);
        var rpm = RpmFiles.Read(result.PackagePath);
        Assert.Equal("aarch64", rpm.Main.String(RpmTag.Arch));
        Assert.Equal(["notes", "notes(aarch-64)"], rpm.Main.Strings(RpmTag.ProvideName));
        Assert.Equal(19, (rpm.Lead[8] << 8) | rpm.Lead[9]);
    }

    [Fact]
    public void Pack_Prerelease_HasATildeInTheVersionAndADotInTheFileName()
    {
        var result = Pack(Config(version: "2.0.0-rc.1+ci.7"));

        Assert.Equal(Path.Combine(Dist, "notes-2.0.0.rc.1-1.x86_64.rpm"), result.PackagePath);
        var rpm = RpmFiles.Read(result.PackagePath);
        Assert.Equal("2.0.0~rc.1", rpm.Main.String(RpmTag.Version));
        Assert.StartsWith("notes-2.0.0~rc.1-1\0", Encoding.ASCII.GetString(rpm.Lead, 10, 66), StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_PrereleaseWithHyphen_Fails()
    {
        var error = Assert.Throws<LinuxPackException>(() => Pack(Config(version: "1.0.0-rc-1")));
        Assert.StartsWith("app.version: the pre-release identifier 'rc-1'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_InvalidRequirement_FailsNamingTheKey()
    {
        var error = Assert.Throws<LinuxPackException>(() => Pack(Config("[linux.rpm]\nrequires = [\"bash\", \"coreutils>=8\"]")));
        Assert.StartsWith("linux.rpm.requires[1]: 'coreutils>=8'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_Twice_IsByteIdentical()
    {
        var first = File.ReadAllBytes(Pack(Config(UpdatesTable)).PackagePath);
        var second = File.ReadAllBytes(Pack(Config(UpdatesTable)).PackagePath);

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3-beta.1", "1.2.3~beta.1")]
    [InlineData("1.2.3-rc.1+build.5", "1.2.3~rc.1")]
    [InlineData("1.2.3+build.5", "1.2.3")]
    public void RpmVersion_From_MapsSemanticVersions(string semver, string expected)
    {
        Assert.Equal(expected, RpmVersion.From(SemanticVersion.Parse(semver)));
    }

    [Theory]
    [InlineData("1.0.0~alpha.1", "1.0.0~alpha.2")]
    [InlineData("1.0.0~alpha.2", "1.0.0~beta.1")]
    [InlineData("1.0.0~beta.11", "1.0.0~rc.1")]
    [InlineData("1.0.0~rc.1", "1.0.0")]
    [InlineData("1.0.0", "1.0.1~alpha.1")]
    [InlineData("1.9.0", "1.10.0")]
    public void RpmVersion_ConsistentPrereleases_SortAsInSemVer(string lower, string higher)
    {
        Assert.True(RpmVercmp(lower, higher) < 0, $"{lower} < {higher}");
        Assert.True(RpmVercmp(higher, lower) > 0, $"{higher} > {lower}");
        var semverLower = SemanticVersion.Parse(lower.Replace('~', '-'));
        var semverHigher = SemanticVersion.Parse(higher.Replace('~', '-'));
        Assert.True(semverLower.CompareTo(semverHigher) < 0);
    }

    [Theory]
    [InlineData("bash", "bash", 0, "")]
    [InlineData("coreutils >= 8.0", "coreutils", 12, "8.0")]
    [InlineData("foo < 2", "foo", 2, "2")]
    [InlineData("foo <= 2", "foo", 10, "2")]
    [InlineData("foo = 1:2-3", "foo", 8, "1:2-3")]
    [InlineData("foo > 2", "foo", 4, "2")]
    [InlineData("libssl.so.3()(64bit)", "libssl.so.3()(64bit)", 0, "")]
    [InlineData("(a or b)", "(a or b)", 0, "")]
    public void Dependency_Parse_ReadsSpecFileSyntax(string text, string name, int flags, string version)
    {
        var dependency = RpmPackager.Dependency.Parse(text, "key");

        Assert.Equal(name, dependency.Name);
        Assert.Equal(flags, (int)dependency.Flags);
        Assert.Equal(version, dependency.Version);
    }

    [Theory]
    [InlineData("foo >=")]
    [InlineData("foo ~> 2")]
    [InlineData("a, b")]
    [InlineData("(a or b")]
    [InlineData("foo>=2")]
    public void Dependency_Parse_RejectsOtherText(string text)
    {
        Assert.Throws<LinuxPackException>(() => RpmPackager.Dependency.Parse(text, "key"));
    }

    private static List<string> FilePaths(RpmFiles.Header main)
    {
        var dirs = main.Strings(RpmTag.DirNames);
        return main.Numbers(RpmTag.DirIndexes).Zip(main.Strings(RpmTag.BaseNames), (d, b) => dirs[d] + b).ToList();
    }

    /// <summary>rpm's rpmvercmp (rpmio/rpmvercmp.c), for versions without '^'.</summary>
    private static int RpmVercmp(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length || j < b.Length)
        {
            while (i < a.Length && !char.IsAsciiLetterOrDigit(a[i]) && a[i] != '~')
            {
                i++;
            }

            while (j < b.Length && !char.IsAsciiLetterOrDigit(b[j]) && b[j] != '~')
            {
                j++;
            }

            if ((i < a.Length && a[i] == '~') || (j < b.Length && b[j] == '~'))
            {
                if (i >= a.Length || a[i] != '~')
                {
                    return 1;
                }

                if (j >= b.Length || b[j] != '~')
                {
                    return -1;
                }

                i++;
                j++;
                continue;
            }

            if (i >= a.Length || j >= b.Length)
            {
                break;
            }

            var numeric = char.IsAsciiDigit(a[i]);
            int si = i, sj = j;
            while (i < a.Length && (numeric ? char.IsAsciiDigit(a[i]) : char.IsAsciiLetter(a[i])))
            {
                i++;
            }

            while (j < b.Length && (numeric ? char.IsAsciiDigit(b[j]) : char.IsAsciiLetter(b[j])))
            {
                j++;
            }

            if (sj == j)
            {
                return numeric ? 1 : -1;
            }

            var x = a[si..i];
            var y = b[sj..j];
            if (numeric)
            {
                x = x.TrimStart('0');
                y = y.TrimStart('0');
                if (x.Length != y.Length)
                {
                    return x.Length > y.Length ? 1 : -1;
                }
            }

            var c = string.CompareOrdinal(x, y);
            if (c != 0)
            {
                return Math.Sign(c);
            }
        }

        if (i >= a.Length && j >= b.Length)
        {
            return 0;
        }

        return i >= a.Length ? -1 : 1;
    }
}
