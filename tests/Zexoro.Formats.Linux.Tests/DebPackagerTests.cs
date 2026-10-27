using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zexoro.Configuration;
using Zexoro.Platforms;
using Zexoro.Versioning;

namespace Zexoro.Formats.Linux.Tests;

public sealed class DebPackagerTests : IDisposable
{
    private static readonly string Key = NewKey();

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-deb-pack-");
    private readonly StringWriter _log = new();

    public DebPackagerTests()
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
        formats = ["deb"]
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

    private DebPackResult Pack(ZexoroConfig config, Architecture? architecture = null) =>
        DebPackager.Pack(new DebPackRequest
        {
            Config = config,
            Architecture = architecture ?? Architecture.X64,
            OutputDirectory = Dist,
            Log = _log,
            ResolveUpdater = runtime => Updater(runtime, TarFiles.Elf(runtime.Platform.Architecture == Architecture.Arm64 ? (ushort)183 : (ushort)62)),
        });

    /// <summary>The members of an ar archive, checking its structure on the way.</summary>
    private static List<(string Name, string Header, byte[] Content)> ReadAr(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("!<arch>\n"u8.ToArray(), bytes[..8]);
        var members = new List<(string, string, byte[])>();
        var offset = 8;
        while (offset < bytes.Length)
        {
            var header = Encoding.ASCII.GetString(bytes, offset, 60);
            Assert.EndsWith("`\n", header, StringComparison.Ordinal);
            var size = int.Parse(header.AsSpan(48, 10).Trim(), CultureInfo.InvariantCulture);
            members.Add((header[..16].TrimEnd(), header, bytes[(offset + 60)..(offset + 60 + size)]));
            offset += 60 + size + (size % 2);
        }

        Assert.Equal(bytes.Length, offset);
        return members;
    }

    private static Dictionary<string, TarFiles.Entry> ReadTar(byte[] gzip)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, gzip);
            return TarFiles.Read(path).ToDictionary(e => e.Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Dictionary<string, string> ControlFields(string control) =>
        control.Split('\n').Where(l => l.Length > 0 && l[0] != ' ').ToDictionary(l => l[..l.IndexOf(':', StringComparison.Ordinal)], l => l[(l.IndexOf(':', StringComparison.Ordinal) + 2)..]);

    [Fact]
    public void Pack_WritesTheArMembersInOrder()
    {
        var result = Pack(Config());

        Assert.Equal(Path.Combine(Dist, "notes_1.2.3_amd64.deb"), result.PackagePath);
        var members = ReadAr(result.PackagePath);
        Assert.Equal(["debian-binary", "control.tar.gz", "data.tar.gz"], members.Select(m => m.Name));
        Assert.Equal("2.0\n"u8.ToArray(), members[0].Content);
        foreach (var (_, header, _) in members)
        {
            Assert.Equal("318211200   0     0     100644  ", header[16..48]);
        }
    }

    [Fact]
    public void Pack_ControlArchive_HoldsControlAndMd5sums()
    {
        var result = Pack(Config());

        var control = ReadTar(ReadAr(result.PackagePath)[1].Content);
        Assert.Equal(["./", "./control", "./md5sums"], control.Keys);
        Assert.All(control.Values, e => Assert.Equal((0, 0, "root", "root"), (e.Uid, e.Gid, e.UserName, e.GroupName)));
        Assert.Equal(result.Control, Encoding.UTF8.GetString(control["./control"].Content));

        var data = ReadTar(ReadAr(result.PackagePath)[2].Content);
#pragma warning disable CA5351 // md5sums is defined with MD5.
        var expected = data.Values
            .Where(e => e.Type == TarEntryType.RegularFile)
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => $"{Convert.ToHexStringLower(MD5.HashData(e.Content))}  {e.Name[2..]}\n");
#pragma warning restore CA5351
        Assert.Equal(string.Concat(expected), Encoding.UTF8.GetString(control["./md5sums"].Content));
    }

    [Fact]
    public void Pack_WithUpdates_WritesTheLayout()
    {
        var result = Pack(Config(UpdatesTable));

        var data = ReadTar(ReadAr(result.PackagePath)[2].Content);
        Assert.Equal(
            [
                "./", "./opt/", "./opt/notes/", "./opt/notes/app/", "./opt/notes/app/data/", "./opt/notes/app/data/readme.txt",
                "./opt/notes/app/lib/", "./opt/notes/app/lib/libhelper.so", "./opt/notes/app/notes", "./opt/notes/zexoro-install.json",
                "./opt/notes/zexoro-updater", "./usr/", "./usr/bin/", "./usr/bin/notes", "./usr/share/", "./usr/share/applications/",
                "./usr/share/applications/com.example.notes.desktop", "./usr/share/icons/", "./usr/share/icons/hicolor/",
                "./usr/share/icons/hicolor/32x32/", "./usr/share/icons/hicolor/32x32/apps/", "./usr/share/icons/hicolor/32x32/apps/com.example.notes.png",
            ],
            data.Keys);
        Assert.All(data.Values, e => Assert.Equal((0, 0, "root", "root"), (e.Uid, e.Gid, e.UserName, e.GroupName)));
        Assert.Equal(TarEntryType.SymbolicLink, data["./usr/bin/notes"].Type);
        Assert.Equal("/opt/notes/app/notes", data["./usr/bin/notes"].LinkName);
        Assert.Equal(TarItem.Executable, data["./opt/notes/zexoro-updater"].Mode);
        Assert.Equal(TarItem.Executable, data["./opt/notes/app/notes"].Mode);
        Assert.Equal(TarItem.Regular, data["./opt/notes/app/data/readme.txt"].Mode);
        Assert.Equal(TarItem.Executable, data["./opt/notes/"].Mode);

        var desktop = Encoding.UTF8.GetString(data["./usr/share/applications/com.example.notes.desktop"].Content);
        Assert.Contains("Exec=/usr/bin/notes\n", desktop, StringComparison.Ordinal);
        Assert.Contains("Icon=com.example.notes\n", desktop, StringComparison.Ordinal);

        using var record = JsonDocument.Parse(data["./opt/notes/zexoro-install.json"].Content);
        var root = record.RootElement;
        Assert.Equal("com.example.notes", root.GetProperty("app").GetString());
        Assert.Equal("1.2.3", root.GetProperty("version").GetString());
        Assert.Equal("linux", root.GetProperty("os").GetString());
        Assert.Equal("x64", root.GetProperty("arch").GetString());
        Assert.Equal("deb", root.GetProperty("format").GetString());
        Assert.Equal("machine", root.GetProperty("scope").GetString());
        Assert.Equal("notify", root.GetProperty("strategy").GetString());
        Assert.Equal("https://example.com/updates/stable/feed.json", root.GetProperty("feed").GetString());
        Assert.Equal("notes", root.GetProperty("main").GetString());
    }

    [Fact]
    public void Pack_WithUpdates_WritesTheControlFields()
    {
        var result = Pack(
            Config(UpdatesTable.Replace("[updates]", "[linux.deb]\ndepends = [\"libicu74 | libicu72\"]\n\n[updates]", StringComparison.Ordinal), app: "url = \"https://example.com/notes\""),
            Architecture.Arm64);

        Assert.Equal(Path.Combine(Dist, "notes_1.2.3_arm64.deb"), result.PackagePath);
        var fields = ControlFields(result.Control);
        Assert.Equal(
            ["Package", "Version", "Architecture", "Maintainer", "Installed-Size", "Depends", "Recommends", "Section", "Priority", "Homepage", "Description"],
            fields.Keys);
        Assert.Equal("notes", fields["Package"]);
        Assert.Equal("1.2.3", fields["Version"]);
        Assert.Equal("arm64", fields["Architecture"]);
        Assert.Equal("Example Ltd.", fields["Maintainer"]);
        Assert.Equal("libc6 (>= 2.28), libssl3t64 | libssl3 | libssl1.1, libicu74 | libicu72", fields["Depends"]);
        Assert.Equal("ca-certificates", fields["Recommends"]);
        Assert.Equal("https://example.com/notes", fields["Homepage"]);
        Assert.Equal("Example Notes", fields["Description"]);
        Assert.True(long.Parse(fields["Installed-Size"], CultureInfo.InvariantCulture) >= 10);
        Assert.Contains("notice: [linux] maintainer is not set", _log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_WithoutUpdates_HasNoUpdaterAndNoUpdaterDepends()
    {
        var result = Pack(Config("maintainer = \"Notes Team <team@example.com>\""));

        var data = ReadTar(ReadAr(result.PackagePath)[2].Content);
        Assert.DoesNotContain("./opt/notes/zexoro-updater", data.Keys);
        var fields = ControlFields(result.Control);
        Assert.False(fields.ContainsKey("Depends"));
        Assert.False(fields.ContainsKey("Recommends"));
        Assert.Equal("Notes Team <team@example.com>", fields["Maintainer"]);
        Assert.DoesNotContain("notice", _log.ToString(), StringComparison.Ordinal);
        using var record = JsonDocument.Parse(data["./opt/notes/zexoro-install.json"].Content);
        Assert.False(record.RootElement.TryGetProperty("feed", out _));
    }

    [Fact]
    public void Pack_Prerelease_HasATildeInTheVersionAndADotInTheFileName()
    {
        var result = Pack(Config(version: "2.0.0-beta.1+ci.7"));

        Assert.Equal(Path.Combine(Dist, "notes_2.0.0.beta.1_amd64.deb"), result.PackagePath);
        Assert.Equal("2.0.0~beta.1", ControlFields(result.Control)["Version"]);
        using var record = JsonDocument.Parse(ReadTar(ReadAr(result.PackagePath)[2].Content)["./opt/notes/zexoro-install.json"].Content);
        Assert.Equal("2.0.0-beta.1+ci.7", record.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void Pack_PrereleaseWithHyphen_Fails()
    {
        var error = Assert.Throws<LinuxPackException>(() => Pack(Config(version: "1.0.0-rc-1")));
        Assert.StartsWith("app.version: the pre-release identifier 'rc-1'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_MultiLineDescription_HasAnExtendedDescription()
    {
        var result = Pack(Config(app: "description = \"Takes notes.\\nFast.\\n\\nAnd small.\""));
        Assert.EndsWith("Description: Takes notes.\n Fast.\n .\n And small.\n", result.Control, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_PublisherWithLineBreak_Fails()
    {
        var config = ZexoroConfig.Parse(File.ReadAllText(WriteConfig()).Replace("Example Ltd.", "Example\\nPackage: evil", StringComparison.Ordinal), _dir.FullName);
        var error = Assert.Throws<LinuxPackException>(() => Pack(config));
        Assert.Equal("app.publisher: the deb Maintainer field must be one line.", error.Message);
    }

    private string WriteConfig()
    {
        var path = Path.Combine(_dir.FullName, "zexoro.toml");
        File.WriteAllText(path, """
            [app]
            id = "com.example.notes"
            name = "Example Notes"
            version = "1.2.3"
            publisher = "Example Ltd."
            icon = "app.ico"

            [input]
            dir = "build"
            main = "notes"
            """);
        return path;
    }

    [Fact]
    public void Pack_TwiceAtDifferentTimes_GivesTheSameBytes()
    {
        var first = File.ReadAllBytes(Pack(Config(UpdatesTable)).PackagePath);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_dir.FullName, "build"), "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-3));
        }

        Assert.Equal(first, File.ReadAllBytes(Pack(Config(UpdatesTable)).PackagePath));
    }

    [Fact]
    public void Pack_X86_Fails() =>
        Assert.Throws<LinuxPackException>(() => Pack(Config(), Architecture.X86));

    [Theory]
    [InlineData(48, 48, "usr/share/icons/hicolor/48x48/apps/com.example.notes.png")]
    [InlineData(40, 40, "usr/share/pixmaps/com.example.notes.png")]
    [InlineData(48, 32, "usr/share/pixmaps/com.example.notes.png")]
    public void IconPath_UsesHicolorForItsSizes(int width, int height, string expected) =>
        Assert.Equal(expected, DebPackager.IconPath("com.example.notes", new PngImage([], width, height)));

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3-beta.1", "1.2.3~beta.1")]
    [InlineData("1.2.3-rc.1+build.5", "1.2.3~rc.1")]
    [InlineData("1.2.3+build.5", "1.2.3")]
    [InlineData("0.0.1-alpha", "0.0.1~alpha")]
    [InlineData("10.20.30-0.3.7", "10.20.30~0.3.7")]
    public void DebVersion_MapsSemanticVersions(string version, string expected) =>
        Assert.Equal(expected, DebVersion.From(SemanticVersion.Parse(version)));

    [Fact]
    public void DebVersion_RejectsHyphensInPrereleases() =>
        Assert.Throws<ArgumentException>(() => DebVersion.From(SemanticVersion.Parse("1.0.0-x-y")));

    [Fact]
    public void DebVersion_KeepsTheOrderOfUsualPrereleases()
    {
        string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.2", "1.0.0-alpha.10", "1.0.0-beta.1", "1.0.0-rc.1", "1.0.0", "1.0.1-rc.1", "1.0.1"];
        var mapped = ordered.Select(v => DebVersion.From(SemanticVersion.Parse(v))).ToList();
        for (var i = 1; i < mapped.Count; i++)
        {
            Assert.True(CompareDebian(mapped[i - 1], mapped[i]) < 0, $"{mapped[i - 1]} < {mapped[i]}");
        }
    }

    /// <summary>dpkg's upstream version comparison (deb-version(7)), for checking the mapping.</summary>
    private static int CompareDebian(string a, string b)
    {
        static int Order(char c) => c == '~' ? -1 : char.IsAsciiLetter(c) ? c : c + 256;
        var i = 0;
        var j = 0;
        while (i < a.Length || j < b.Length)
        {
            while ((i < a.Length && !char.IsAsciiDigit(a[i])) || (j < b.Length && !char.IsAsciiDigit(b[j])))
            {
                var aLetter = i < a.Length && !char.IsAsciiDigit(a[i]);
                var bLetter = j < b.Length && !char.IsAsciiDigit(b[j]);
                var ac = aLetter ? Order(a[i]) : 0;
                var bc = bLetter ? Order(b[j]) : 0;
                if (ac != bc)
                {
                    return ac - bc;
                }

                i += aLetter ? 1 : 0;
                j += bLetter ? 1 : 0;
            }

            long an = 0;
            long bn = 0;
            while (i < a.Length && char.IsAsciiDigit(a[i]))
            {
                an = (an * 10) + a[i++] - '0';
            }

            while (j < b.Length && char.IsAsciiDigit(b[j]))
            {
                bn = (bn * 10) + b[j++] - '0';
            }

            if (an != bn)
            {
                return an < bn ? -1 : 1;
            }
        }

        return 0;
    }
}
