using System.Security.Cryptography.X509Certificates;
using Zexoro.Formats.Nsis;
using Zexoro.Platforms;
using Zexoro.Signing;
using Zexoro.Signing.Fixtures;

namespace Zexoro.Cli.Tests;

/// <summary>
/// Runs <c>zexoro pack</c> end to end with the NSIS toolset embedded into this build: extraction,
/// staging, script generation, makensis and signing. Installing the result is tested on Windows
/// by the end-to-end workflow.
/// </summary>
public sealed class PackCommandTests : IDisposable
{
    private const string BaseConfig = """
        [app]
        id = "com.example.notes"
        name = "Example Notes"
        version = "1.2.3"
        publisher = "Example Ltd."
        icon = "assets/app.ico"
        url = "https://example.com/notes"

        [input]
        dir = "build/win-x64"
        main = "ExampleNotes.exe"

        """;

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-pack-");
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();
    private readonly Dictionary<string, string> _variables = [];
    private readonly List<string> _stagings = [];

    public PackCommandTests()
    {
        _variables[Zexoro.Toolsets.ToolsetCache.CacheDirectoryVariable] = Path.Combine(_dir.FullName, ".cache");
    }

    public void Dispose()
    {
        _stdout.Dispose();
        _stderr.Dispose();
        _dir.Delete(recursive: true);
        foreach (var staging in _stagings)
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    private string Stdout => _stdout.ToString();

    private string Stderr => _stderr.ToString();

    private string Dist => Path.Combine(_dir.FullName, "dist");

    private int Run(params string[] args) =>
        App.Run(args, _stdout, _stderr, new CliEnvironment(_dir.FullName, name => _variables.GetValueOrDefault(name))
        {
            ZexoroCommand = DotnetZexoroCommand(),
        });

    /// <summary>Runs this build's zexoro.dll with the dotnet host that runs the tests, for <c>sign-file</c>.</summary>
    private static string[] DotnetZexoroCommand()
    {
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var dotnet = Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        return [dotnet, "exec", Path.Combine(AppContext.BaseDirectory, "zexoro.dll")];
    }

    private void WriteFile(string relative, byte[] content)
    {
        var path = Path.Combine(_dir.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private void WriteProject(string windows, bool pe32Plus = true)
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "zexoro.toml"), BaseConfig + windows);
        WriteFile("build/win-x64/ExampleNotes.exe", TestPe.Build(pe32Plus));
        WriteFile("build/win-x64/lib/helper.dll", TestPe.Build(pe32Plus, seed: 2));
        WriteFile("build/win-x64/data/readme.txt", "hello"u8.ToArray());
        Directory.CreateDirectory(Path.Combine(_dir.FullName, "build/win-x64/logs"));
        WriteFile("assets/app.ico", TestIcon.Create());
    }

    private static void AssertInstaller(string path)
    {
        Assert.True(File.Exists(path), $"{path} was not created");
        Assert.Equal(Architecture.X86, PeMachine.Read(path)); // the NSIS stub is x86 on every target
        Assert.True(new FileInfo(path).Length > 30_000);
    }

    [Fact]
    public void Pack_UserScope_BuildsInstallerFromTheEmbeddedToolset()
    {
        WriteProject("[windows]\n");

        Assert.Equal(ExitCodes.Success, Run("pack"));

        Assert.Equal(string.Empty, Stderr);
        Assert.Contains("notice: [windows.signing] is not configured", Stdout, StringComparison.Ordinal);
        Assert.Contains("bundled NSIS", Stdout, StringComparison.Ordinal);
        AssertInstaller(Path.Combine(Dist, "ExampleNotes-1.2.3-win-x64.exe"));
        Assert.False(AuthenticodeSigner.IsSigned(Path.Combine(Dist, "ExampleNotes-1.2.3-win-x64.exe")));
    }

    [Fact]
    public void Pack_MachineScopeWithEverything_Builds()
    {
        WriteFile("LICENSE.txt", "License text with Ünïcødé\n"u8.ToArray());
        WriteProject("""
            [windows]
            install_scope = "machine"
            shortcuts = ["start-menu", "desktop"]
            license = "LICENSE.txt"

            [windows.uninstall]
            remove = ["%LOCALAPPDATA%\\Example Notes", "%PROGRAMDATA%/Example Notes"]

            """, pe32Plus: false);

        Assert.Equal(ExitCodes.Success, Run("pack", "--version", "2.0.0-beta.1", "--output", "out"));

        AssertInstaller(Path.Combine(_dir.FullName, "out", "ExampleNotes-2.0.0-beta.1-win-x86.exe"));
    }

    [Fact]
    public void Pack_WithCertificate_SignsApplicationUninstallerAndInstaller()
    {
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa: false);
        WriteFile("signing/cert.pfx", certificate.Export(X509ContentType.Pkcs12, "pfx-pass"));
        _variables["CERT_PASSWORD"] = "pfx-pass";
        WriteProject("""
            [windows]
            sign = "all"

            [windows.signing]
            certificate = "signing/cert.pfx"
            password = { env = "CERT_PASSWORD" }

            """);

        var exit = Run("pack");

        Assert.True(exit == ExitCodes.Success, Stdout + Stderr);
        Assert.Contains("Signing ExampleNotes.exe", Stdout, StringComparison.Ordinal);
        Assert.Contains("Signing lib/helper.dll", Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("pfx-pass", Stdout + Stderr, StringComparison.Ordinal);
        var installer = Path.Combine(Dist, "ExampleNotes-1.2.3-win-x64.exe");
        AssertInstaller(installer);
        Assert.True(AuthenticodeSigner.IsSigned(installer));

        // The build output itself is left untouched; only the staged copies are signed.
        Assert.False(AuthenticodeSigner.IsSigned(Path.Combine(_dir.FullName, "build/win-x64/ExampleNotes.exe")));
    }

    [Fact]
    public void Pack_WrongCertificatePassword_FailsBeforeBuilding()
    {
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa: true);
        WriteFile("cert.pfx", certificate.Export(X509ContentType.Pkcs12, "right"));
        WriteProject("""
            [windows]

            [windows.signing]
            certificate = "cert.pfx"
            password = "wrong"

            """);

        Assert.Equal(ExitCodes.Failure, Run("pack"));
        Assert.Contains("windows.signing.certificate", Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Dist));
    }

    [Fact]
    public void Pack_NoConfiguration_SuggestsInit()
    {
        Assert.Equal(ExitCodes.Failure, Run("pack"));
        Assert.Contains("zexoro init", Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_InvalidConfiguration_ListsErrors()
    {
        File.WriteAllText(Path.Combine(_dir.FullName, "zexoro.toml"), BaseConfig.Replace("1.2.3", "one", StringComparison.Ordinal));
        Assert.Equal(ExitCodes.Failure, Run("pack"));
        Assert.Contains("zexoro.toml:4:", Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_NoWindowsTable_BuildsTheDefaultInstallerForAWindowsMain()
    {
        WriteProject(string.Empty);

        Assert.Equal(ExitCodes.Success, Run("pack"));

        Assert.Contains("Packing Example Notes 1.2.3 (com.example.notes) for win-x64: nsis", Stdout, StringComparison.Ordinal);
        AssertInstaller(Path.Combine(Dist, "ExampleNotes-1.2.3-win-x64.exe"));
    }

    [Fact]
    public void Pack_FormatOption_SelectsTheFormat()
    {
        WriteProject("[windows]\nformats = [\"nsis\"]\n");

        Assert.Equal(ExitCodes.Success, Run("pack", "--format", "nsis"));

        AssertInstaller(Path.Combine(Dist, "ExampleNotes-1.2.3-win-x64.exe"));
    }

    [Theory]
    [InlineData("msi", "the windows format 'msi' is not implemented yet; this zexoro version packs nsis, zip for windows")]
    [InlineData("nsis,msi", "the windows format 'msi' is not implemented yet")]
    [InlineData("deb", "--format: 'deb' is a linux format, but the target is win-x64 (input.main: a Windows x64 executable (PE)); formats for windows: nsis, zip, msi")]
    [InlineData("nsi", "--format: unknown format 'nsi'; formats for windows: nsis, zip, msi; did you mean 'nsis'?")]
    [InlineData("nsis,nsis", "--format: 'nsis' is listed twice")]
    [InlineData("nsis,", "--format: 'nsis,' contains an empty format name")]
    public void Pack_InvalidOrUnimplementedFormat_FailsBeforePacking(string formats, string message)
    {
        WriteProject("[windows]\n");

        Assert.Equal(ExitCodes.Failure, Run("pack", "--format", formats));

        Assert.Contains(message, Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Dist));
    }

    [Fact]
    public void Pack_UnimplementedFormatInConfiguration_FailsBeforePacking()
    {
        WriteProject("[windows]\nformats = [\"nsis\", \"msi\"]\n");

        Assert.Equal(ExitCodes.Failure, Run("pack"));

        Assert.Contains("the windows format 'msi' is not implemented yet", Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Dist));
    }

    [Fact]
    public void Pack_LinuxTarget_ValidatesButIgnoresTheWindowsTable()
    {
        WriteProject("[windows]\nformats = [\"msi\"]\n\n[linux]\nformats = [\"appimage\"]\n");
        WriteFile("build/linux/notes", ElfHeader());

        Assert.Equal(ExitCodes.Failure, Run("pack", "--input", "build/linux", "--main", "notes"));

        Assert.Equal("zexoro: the linux format 'appimage' is not implemented yet; this zexoro version packs tar.gz, deb, rpm, apk for linux" + Environment.NewLine, Stderr);
    }

    [Fact]
    public void Pack_InputAndMainOptions_ReplaceTheConfiguredOnes()
    {
        WriteProject("[windows]\n");
        Directory.Move(Path.Combine(_dir.FullName, "build", "win-x64"), Path.Combine(_dir.FullName, "out-x64"));
        File.Move(Path.Combine(_dir.FullName, "out-x64", "ExampleNotes.exe"), Path.Combine(_dir.FullName, "out-x64", "Notes.exe"));

        Assert.Equal(ExitCodes.Success, Run("pack", "--input", "out-x64", "--main", "Notes.exe"));

        AssertInstaller(Path.Combine(Dist, "ExampleNotes-1.2.3-win-x64.exe"));
    }

    [Fact]
    public void Pack_MainOptionInvalid_Fails()
    {
        WriteProject("[windows]\n");

        Assert.Equal(ExitCodes.Failure, Run("pack", "--main", "../x.exe"));

        Assert.Contains("zexoro: --main: '../x.exe' must not contain '.' or '..' segments", Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_TargetContradictingTheMain_Fails()
    {
        WriteProject("[windows]\n");
        File.WriteAllText(Path.Combine(_dir.FullName, "zexoro.toml"), BaseConfig.Replace("main = \"ExampleNotes.exe\"", "main = \"ExampleNotes.exe\"\ntarget = \"win-arm64\"", StringComparison.Ordinal));

        Assert.Equal(ExitCodes.Failure, Run("pack"));

        Assert.Contains("input.target: 'win-arm64' does not match input.main", Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_ScriptMainWithWindowsTarget_BuildsTheInstaller()
    {
        WriteProject("[windows]\n");
        File.WriteAllText(Path.Combine(_dir.FullName, "zexoro.toml"), BaseConfig.Replace("main = \"ExampleNotes.exe\"", "main = \"notes.cmd\"\ntarget = \"win-arm64\"", StringComparison.Ordinal));
        WriteFile("build/win-x64/notes.cmd", "@echo off\r\n"u8.ToArray());

        Assert.Equal(ExitCodes.Success, Run("pack"));

        AssertInstaller(Path.Combine(Dist, "ExampleNotes-1.2.3-win-arm64.exe"));
    }

    /// <summary>A minimal 64-bit little-endian ELF header for x86-64.</summary>
    private static byte[] ElfHeader()
    {
        var header = new byte[64];
        "\u007FELF"u8.CopyTo(header);
        header[4] = 2;
        header[5] = 1;
        header[6] = 1;
        header[16] = 3;
        header[18] = 62;
        return header;
    }

    [Fact]
    public void Pack_VersionTooLargeForWindows_Fails()
    {
        WriteProject("[windows]\n");
        Assert.Equal(ExitCodes.Failure, Run("pack", "--version", "1.70000.0"));
        Assert.Contains("65535", Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_MainNotAnExecutable_Fails()
    {
        WriteProject("[windows]\n");
        WriteFile("build/win-x64/ExampleNotes.exe", "#!/bin/sh"u8.ToArray());
        Assert.Equal(ExitCodes.Failure, Run("pack"));
        Assert.Contains("is not a PE, ELF or Mach-O executable, so the target platform is unknown", Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_MissingInputDirectory_Fails()
    {
        WriteProject("[windows]\n");
        Directory.Delete(Path.Combine(_dir.FullName, "build"), recursive: true);
        Assert.Equal(ExitCodes.Failure, Run("pack"));
        Assert.Contains("build your application first", Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_ToolsetDirectoryOverrideMissing_Fails()
    {
        WriteProject("[windows]\n");
        _variables[NsisToolset.DirectoryVariable] = Path.Combine(_dir.FullName, "no-nsis");
        Assert.Equal(ExitCodes.Failure, Run("pack"));
        Assert.Contains(NsisToolset.DirectoryVariable, Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--config")]
    [InlineData("--output=")]
    [InlineData("--version", "1.0.0", "--version", "2.0.0")]
    [InlineData("--format")]
    [InlineData("--format", "nsis", "--format", "zip")]
    [InlineData("--input=")]
    [InlineData("--main")]
    public void Pack_BadArguments_IsUsageError(params string[] args)
    {
        Assert.Equal(ExitCodes.UsageError, Run(["pack", .. args]));
        Assert.Contains("zexoro --help", Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_ReportsTheBundledToolset()
    {
        WriteProject("[windows]\n");
        Run("doctor");
        Assert.Contains("bundled NSIS", Stdout, StringComparison.Ordinal);
        Assert.Contains("makensis v3.13", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void SignFile_SignsWithPasswordFromEnvironment()
    {
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa: true);
        WriteFile("cert.pfx", certificate.Export(X509ContentType.Pkcs12, "secret"));
        WriteFile("target.exe", TestPe.Build(pe32Plus: true));
        new SignFileRequest { Certificate = Path.Combine(_dir.FullName, "cert.pfx"), Description = "Example" }
            .Write(Path.Combine(_dir.FullName, "request.json"));
        _variables[SignFileRequest.PasswordVariable] = "secret";

        Assert.Equal(ExitCodes.Success, Run("sign-file", "--request", "request.json", "target.exe"));
        Assert.True(AuthenticodeSigner.IsSigned(Path.Combine(_dir.FullName, "target.exe")));
    }

    [Fact]
    public void SignFile_BadRequest_Fails()
    {
        WriteFile("target.exe", TestPe.Build(pe32Plus: true));
        WriteFile("request.json", "{}"u8.ToArray());
        Assert.Equal(ExitCodes.Failure, Run("sign-file", "--request", "request.json", "target.exe"));
        Assert.Contains("cannot sign", Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("target.exe")]
    [InlineData("--request", "r.json")]
    [InlineData("--request", "r.json", "a.exe", "b.exe")]
    public void SignFile_BadArguments_IsUsageError(params string[] args) =>
        Assert.Equal(ExitCodes.UsageError, Run(["sign-file", .. args]));

    private const string UpdatesTable = """

        [updates]
        feed = "https://example.com/updates/{channel}/feed.json"
        download = "https://example.com/dl/{channel}/{file}"
        keys = [{ file = "keys/zexoro-update.pub" }]

        """;

    /// <summary>A key pair from <c>keys new</c> and a fake x64 updater in <c>ZEXORO_UPDATER_DIR</c>.</summary>
    private void PrepareUpdates()
    {
        Assert.Equal(ExitCodes.Success, Run("keys", "new", "--output", "keys"));
        WriteFile("updaters/zexoro-updater-win-x64.exe", TestPe.Build(pe32Plus: true, seed: 9));
        _variables[UpdaterBinary.DirectoryVariable] = Path.Combine(_dir.FullName, "updaters");
    }

    [Fact]
    public void Pack_WithUpdates_WritesAReleaseFragmentThatFeedAddAccepts()
    {
        PrepareUpdates();
        WriteProject("[windows]\n" + UpdatesTable);

        Assert.Equal(ExitCodes.Success, Run("pack", "--channel", "beta", "--notes", "https://example.com/notes/1.2.3"));

        Assert.Equal(string.Empty, Stderr);
        Assert.Contains("Adding zexoro-updater.exe", Stdout, StringComparison.Ordinal);
        var installer = Path.Combine(Dist, "ExampleNotes-1.2.3-win-x64.exe");
        AssertInstaller(installer);
        var fragment = Zexoro.Updates.ReleaseFragment.Parse(File.ReadAllBytes(installer + ".release.json"));
        Assert.Equal("com.example.notes", fragment.App);
        Assert.Equal("beta", fragment.Channel);
        Assert.Equal("1.2.3", fragment.Release.Version.ToString());
        Assert.Equal("https://example.com/notes/1.2.3", fragment.Release.Notes);
        Assert.Equal(NsisScript.MinimumUpdater, fragment.Release.MinimumUpdater?.ToString());
        var artifact = Assert.Single(fragment.Release.Artifacts);
        Assert.Equal(new Zexoro.Updates.UpdateTarget("windows", "x64", "nsis", "user"), artifact.Target);
        Assert.Equal(new Uri("https://example.com/dl/beta/ExampleNotes-1.2.3-win-x64.exe"), artifact.Url);
        Assert.Equal(new FileInfo(installer).Length, artifact.Size);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(installer))), artifact.Sha256);

        Assert.Equal(ExitCodes.Success, Run("feed", "add", "--feed", "feed/beta.json", "--key", "keys/zexoro-update.key.pem", installer + ".release.json"));
        Assert.Equal(ExitCodes.Success, Run("feed", "verify", "--public-key", "keys/zexoro-update.pub", "feed/beta.json"));
    }

    [Fact]
    public void Pack_LinuxTarGzWithUpdates_WritesTheArchiveAndAReleaseFragmentThatFeedAddAccepts()
    {
        Assert.Equal(ExitCodes.Success, Run("keys", "new", "--output", "keys"));
        WriteFile("updaters/zexoro-updater-linux-x64", ElfHeader());
        _variables[UpdaterBinary.DirectoryVariable] = Path.Combine(_dir.FullName, "updaters");
        WriteProject("[linux]\ncategories = [\"Office\"]\n" + UpdatesTable);
        WriteFile("build/linux/notes", ElfHeader());

        Assert.Equal(ExitCodes.Success, Run("pack", "--input", "build/linux", "--main", "notes", "--notes", "https://example.com/notes/1.2.3"));

        Assert.Equal(string.Empty, Stderr);
        Assert.Contains("for linux-x64: tar.gz", Stdout, StringComparison.Ordinal);
        Assert.Contains("Adding zexoro-updater (updater for linux-x64 from ZEXORO_UPDATER_DIR", Stdout, StringComparison.Ordinal);
        var archive = Path.Combine(Dist, "ExampleNotes-1.2.3-linux-x64.tar.gz");
        Assert.Equal([archive], Directory.GetFiles(Dist, "*.tar.gz"));
        var fragment = Zexoro.Updates.ReleaseFragment.Parse(File.ReadAllBytes(archive + ".release.json"));
        Assert.Equal(Zexoro.Formats.Linux.LinuxTarPackager.MinimumUpdater, fragment.Release.MinimumUpdater?.ToString());
        var artifact = Assert.Single(fragment.Release.Artifacts);
        Assert.Equal(new Zexoro.Updates.UpdateTarget("linux", "x64", "tar-gz", "user"), artifact.Target);
        Assert.Equal(new Uri("https://example.com/dl/stable/ExampleNotes-1.2.3-linux-x64.tar.gz"), artifact.Url);
        Assert.Equal(new FileInfo(archive).Length, artifact.Size);

        Assert.Equal(ExitCodes.Success, Run("feed", "add", "--feed", "feed/stable.json", "--key", "keys/zexoro-update.key.pem", archive + ".release.json"));
        Assert.Equal(ExitCodes.Success, Run("feed", "verify", "--public-key", "keys/zexoro-update.pub", "feed/stable.json"));
    }

    [Fact]
    public void Pack_LinuxDebWithUpdates_WritesThePackageAndAReleaseFragmentThatFeedAddAccepts()
    {
        Assert.Equal(ExitCodes.Success, Run("keys", "new", "--output", "keys"));
        WriteFile("updaters/zexoro-updater-linux-x64", ElfHeader());
        _variables[UpdaterBinary.DirectoryVariable] = Path.Combine(_dir.FullName, "updaters");
        WriteProject("[linux]\nformats = [\"deb\"]\nmaintainer = \"Example <dev@example.com>\"\n" + UpdatesTable);
        WriteFile("build/linux/notes", ElfHeader());

        Assert.Equal(ExitCodes.Success, Run("pack", "--input", "build/linux", "--main", "notes"));

        Assert.Equal(string.Empty, Stderr);
        Assert.Contains("for linux-x64: deb", Stdout, StringComparison.Ordinal);
        var package = Assert.Single(Directory.GetFiles(Dist, "*.deb"));
        Assert.Matches(@"_1\.2\.3_amd64\.deb$", package);
        var fragment = Zexoro.Updates.ReleaseFragment.Parse(File.ReadAllBytes(package + ".release.json"));
        Assert.Equal(Zexoro.Formats.Linux.DebPackager.MinimumUpdater, fragment.Release.MinimumUpdater?.ToString());
        var artifact = Assert.Single(fragment.Release.Artifacts);
        Assert.Equal(new Zexoro.Updates.UpdateTarget("linux", "x64", "deb", "machine"), artifact.Target);
        Assert.Equal(new Uri("https://example.com/dl/stable/" + Path.GetFileName(package)), artifact.Url);

        Assert.Equal(ExitCodes.Success, Run("feed", "add", "--feed", "feed/stable.json", "--key", "keys/zexoro-update.key.pem", package + ".release.json"));
        Assert.Equal(ExitCodes.Success, Run("feed", "verify", "--public-key", "keys/zexoro-update.pub", "feed/stable.json"));
    }

    [Fact]
    public void Pack_LinuxApkSigned_WritesASignedPackageWithTheMuslUpdaterAndAReleaseFragment()
    {
        Assert.Equal(ExitCodes.Success, Run("keys", "new", "--output", "keys"));
        WriteFile("updaters/zexoro-updater-linux-musl-x64", ElfHeader());
        _variables[UpdaterBinary.DirectoryVariable] = Path.Combine(_dir.FullName, "updaters");
        _variables["APK_KEY"] = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "zexoro-e2e.rsa"));
        WriteProject(
            "[linux]\nformats = [\"apk\"]\n\n[linux.apk]\nlicense = \"MIT\"\n\n[linux.apk.signing]\nkey = { env = \"APK_KEY\" }\nkey_name = \"zexoro-e2e.rsa.pub\"\n" + UpdatesTable);
        WriteFile("build/linux/notes", ElfHeader());

        Assert.Equal(ExitCodes.Success, Run("pack", "--input", "build/linux", "--main", "notes"));

        Assert.Equal(string.Empty, Stderr);
        Assert.Contains("Signing the apk with RSA 2048-bit (zexoro-e2e.rsa.pub)", Stdout, StringComparison.Ordinal);
        var package = Assert.Single(Directory.GetFiles(Dist, "*.apk"));
        Assert.EndsWith("-1.2.3-r0.x86_64.apk", package, StringComparison.Ordinal);
        var first = File.ReadAllBytes(package);
        var fragment = Zexoro.Updates.ReleaseFragment.Parse(File.ReadAllBytes(package + ".release.json"));
        var artifact = Assert.Single(fragment.Release.Artifacts);
        Assert.Equal(new Zexoro.Updates.UpdateTarget("linux", "x64", "apk", "machine"), artifact.Target);
        Assert.Equal(Zexoro.Formats.Linux.ApkPackager.MinimumUpdater, fragment.Release.MinimumUpdater?.ToString());
        Assert.Equal(ExitCodes.Success, Run("feed", "add", "--feed", "feed/stable.json", "--key", "keys/zexoro-update.key.pem", package + ".release.json"));

        // RSA PKCS #1 v1.5 signatures are deterministic and the package has no time in it.
        Assert.Equal(ExitCodes.Success, Run("pack", "--input", "build/linux", "--main", "notes"));
        Assert.Equal(first, File.ReadAllBytes(package));
    }

    [Fact]
    public void Pack_LinuxApkWithAPublicKey_FailsBeforeWriting()
    {
        _variables["APK_KEY"] = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "zexoro-e2e.rsa.pub"));
        WriteProject("[linux]\nformats = [\"apk\"]\n\n[linux.apk.signing]\nkey = { env = \"APK_KEY\" }\nkey_name = \"zexoro-e2e.rsa.pub\"\n");
        WriteFile("build/linux/notes", ElfHeader());

        Assert.Equal(ExitCodes.Failure, Run("pack", "--input", "build/linux", "--main", "notes"));

        Assert.StartsWith("zexoro: linux.apk.signing.key: the apk signing key is a public key", Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Dist) && Directory.GetFiles(Dist, "*.apk").Length > 0);
    }

    [Fact]
    public void Pack_LinuxRpmSigned_WritesASignedPackageAndAReleaseFragment()
    {
        Assert.Equal(ExitCodes.Success, Run("keys", "new", "--output", "keys"));
        WriteFile("updaters/zexoro-updater-linux-x64", ElfHeader());
        _variables[UpdaterBinary.DirectoryVariable] = Path.Combine(_dir.FullName, "updaters");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "rpm-signing.key.asc"), Path.Combine(_dir.FullName, "rpm.asc"));
        _variables["RPM_PASSWORD"] = "e2e-password";
        _variables["SOURCE_DATE_EPOCH"] = "1780000000";
        WriteProject(
            "[linux]\nformats = [\"rpm\"]\n\n[linux.rpm]\nlicense = \"MIT\"\n\n[linux.rpm.signing]\nkey = \"rpm.asc\"\npassword = { env = \"RPM_PASSWORD\" }\n" + UpdatesTable);
        WriteFile("build/linux/notes", ElfHeader());

        Assert.Equal(ExitCodes.Success, Run("pack", "--input", "build/linux", "--main", "notes"));

        Assert.Equal(string.Empty, Stderr);
        Assert.Contains("Signing the rpm header with RSA 4096 key 1BF4EAF6A3E2C9671AC00DEAE2AB728735D9A497", Stdout, StringComparison.Ordinal);
        var package = Assert.Single(Directory.GetFiles(Dist, "*.rpm"));
        Assert.EndsWith("-1.2.3-1.x86_64.rpm", package, StringComparison.Ordinal);
        var first = File.ReadAllBytes(package);
        var fragment = Zexoro.Updates.ReleaseFragment.Parse(File.ReadAllBytes(package + ".release.json"));
        var artifact = Assert.Single(fragment.Release.Artifacts);
        Assert.Equal(new Zexoro.Updates.UpdateTarget("linux", "x64", "rpm", "machine"), artifact.Target);
        Assert.Equal(Zexoro.Formats.Linux.RpmPackager.MinimumUpdater, fragment.Release.MinimumUpdater?.ToString());
        Assert.Equal(ExitCodes.Success, Run("feed", "add", "--feed", "feed/stable.json", "--key", "keys/zexoro-update.key.pem", package + ".release.json"));

        // With SOURCE_DATE_EPOCH the signature time is fixed and RSA signatures are deterministic.
        Assert.Equal(ExitCodes.Success, Run("pack", "--input", "build/linux", "--main", "notes"));
        Assert.Equal(first, File.ReadAllBytes(package));
    }

    [Theory]
    [InlineData("nope", "linux.rpm.signing.key: The password of the OpenPGP key 1BF4EAF6A3E2C9671AC00DEAE2AB728735D9A497 is wrong.")]
    [InlineData(null, "linux.rpm.signing.key: The OpenPGP key 1BF4EAF6A3E2C9671AC00DEAE2AB728735D9A497 is protected by a passphrase; set its password in the configuration.")]
    public void Pack_LinuxRpmWithAWrongPassword_FailsBeforeWriting(string? password, string message)
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "rpm-signing.key.asc"), Path.Combine(_dir.FullName, "rpm.asc"));
        _variables["RPM_KEY"] = File.ReadAllText(Path.Combine(_dir.FullName, "rpm.asc"));
        var passwordLine = password is null ? string.Empty : $"password = \"{password}\"\n";
        WriteProject("[linux]\nformats = [\"rpm\"]\n\n[linux.rpm.signing]\nkey = { env = \"RPM_KEY\" }\n" + passwordLine);
        WriteFile("build/linux/notes", ElfHeader());

        Assert.Equal(ExitCodes.Failure, Run("pack", "--input", "build/linux", "--main", "notes"));

        Assert.Equal("zexoro: " + message + Environment.NewLine, Stderr);
        Assert.False(Directory.Exists(Dist) && Directory.GetFiles(Dist, "*.rpm").Length > 0);
    }

    [Fact]
    public void Pack_LinuxRpmSignedBeforeTheKeyExisted_Fails()
    {
        File.Copy(Path.Combine(AppContext.BaseDirectory, "rpm-signing.key.asc"), Path.Combine(_dir.FullName, "rpm.asc"));
        _variables["SOURCE_DATE_EPOCH"] = "1000000000";
        WriteProject("[linux]\nformats = [\"rpm\"]\n\n[linux.rpm.signing]\nkey = \"rpm.asc\"\npassword = \"e2e-password\"\n");
        WriteFile("build/linux/notes", ElfHeader());

        Assert.Equal(ExitCodes.Failure, Run("pack", "--input", "build/linux", "--main", "notes"));

        Assert.Contains("lies before the OpenPGP key", Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_LinuxTarGzWithoutAnUpdater_FailsBeforeWriting()
    {
        Assert.Equal(ExitCodes.Success, Run("keys", "new", "--output", "keys"));
        Directory.CreateDirectory(Path.Combine(_dir.FullName, "updaters"));
        _variables[UpdaterBinary.DirectoryVariable] = Path.Combine(_dir.FullName, "updaters");
        WriteProject("[linux]\n" + UpdatesTable);
        WriteFile("build/linux/notes", ElfHeader());

        Assert.Equal(ExitCodes.Failure, Run("pack", "--input", "build/linux", "--main", "notes"));

        Assert.Contains("zexoro-updater-linux-x64 does not exist", Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Dist));
    }

    [Fact]
    public void Pack_ZipWithUpdates_WritesTheArchiveAndAReleaseFragmentThatFeedAddAccepts()
    {
        PrepareUpdates();
        WriteProject("[windows]\nformats = [\"zip\"]\n" + UpdatesTable);

        Assert.Equal(ExitCodes.Success, Run("pack", "--notes", "https://example.com/notes/1.2.3"));

        Assert.Equal(string.Empty, Stderr);
        Assert.Contains("for win-x64: zip", Stdout, StringComparison.Ordinal);
        Assert.Contains("notice: [windows.signing] is not configured; the application in the zip archive is not signed.", Stdout, StringComparison.Ordinal);
        var archive = Path.Combine(Dist, "ExampleNotes-1.2.3-win-x64.zip");
        Assert.Equal([archive], Directory.GetFiles(Dist, "*.zip"));
        Assert.Empty(Directory.GetFiles(Dist, "*.exe"));
        using (var zip = System.IO.Compression.ZipFile.OpenRead(archive))
        {
            Assert.NotNull(zip.GetEntry("ExampleNotes/zexoro-updater.exe"));
            Assert.NotNull(zip.GetEntry("ExampleNotes/app/ExampleNotes.exe"));
        }

        var fragment = Zexoro.Updates.ReleaseFragment.Parse(File.ReadAllBytes(archive + ".release.json"));
        Assert.Equal(Zexoro.Formats.Zip.WindowsZipPackager.MinimumUpdater, fragment.Release.MinimumUpdater?.ToString());
        var artifact = Assert.Single(fragment.Release.Artifacts);
        Assert.Equal(new Zexoro.Updates.UpdateTarget("windows", "x64", "zip", "user"), artifact.Target);
        Assert.Equal(new Uri("https://example.com/dl/stable/ExampleNotes-1.2.3-win-x64.zip"), artifact.Url);
        Assert.Equal(new FileInfo(archive).Length, artifact.Size);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archive))), artifact.Sha256);

        Assert.Equal(ExitCodes.Success, Run("feed", "add", "--feed", "feed/stable.json", "--key", "keys/zexoro-update.key.pem", archive + ".release.json"));
    }

    [Fact]
    public void Pack_NsisAndZip_BuildsBothAndOneFeedTakesBoth()
    {
        PrepareUpdates();
        WriteProject("[windows]\nformats = [\"nsis\", \"zip\"]\n" + UpdatesTable);

        Assert.Equal(ExitCodes.Success, Run("pack"));

        var installer = Path.Combine(Dist, "ExampleNotes-1.2.3-win-x64.exe");
        var archive = Path.Combine(Dist, "ExampleNotes-1.2.3-win-x64.zip");
        AssertInstaller(installer);
        Assert.True(File.Exists(archive));
        Assert.Equal(ExitCodes.Success, Run("feed", "add", "--feed", "feed/stable.json", "--key", "keys/zexoro-update.key.pem", installer + ".release.json", archive + ".release.json"));
        var feed = File.ReadAllText(Path.Combine(_dir.FullName, "feed", "stable.json"));
        Assert.Contains("\"format\": \"nsis\"", feed, StringComparison.Ordinal);
        Assert.Contains("\"format\": \"zip\"", feed, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_ZipWithoutUpdates_WritesNoFragment()
    {
        WriteProject("[windows]\n");

        Assert.Equal(ExitCodes.Success, Run("pack", "--format", "zip"));

        Assert.True(File.Exists(Path.Combine(Dist, "ExampleNotes-1.2.3-win-x64.zip")));
        Assert.Empty(Directory.GetFiles(Dist, "*.release.json"));
    }

    [Fact]
    public void Pack_ZipWithUpdatesWithoutUpdater_FailsClearly()
    {
        PrepareUpdates();
        File.Delete(Path.Combine(_dir.FullName, "updaters", "zexoro-updater-win-x64.exe"));
        WriteProject("[windows]\n" + UpdatesTable);

        Assert.Equal(ExitCodes.Failure, Run("pack", "--format", "zip"));

        Assert.Contains("zexoro-updater-win-x64.exe does not exist", Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Dist));
    }

    [Fact]
    public void Pack_WithUpdatesMachineScope_Builds()
    {
        PrepareUpdates();
        WriteFile("updaters/zexoro-updater-win-x86.exe", TestPe.Build(pe32Plus: false, seed: 10));
        WriteProject("[windows]\ninstall_scope = \"machine\"\n" + UpdatesTable, pe32Plus: false);

        Assert.Equal(ExitCodes.Success, Run("pack"));

        var installer = Path.Combine(Dist, "ExampleNotes-1.2.3-win-x86.exe");
        AssertInstaller(installer);
        var fragment = Zexoro.Updates.ReleaseFragment.Parse(File.ReadAllBytes(installer + ".release.json"));
        Assert.Equal("stable", fragment.Channel);
        Assert.Equal(new Zexoro.Updates.UpdateTarget("windows", "x86", "nsis", "machine"), Assert.Single(fragment.Release.Artifacts).Target);
    }

    [Fact]
    public void Pack_WithUpdatesWithoutUpdater_FailsClearly()
    {
        PrepareUpdates();
        File.Delete(Path.Combine(_dir.FullName, "updaters", "zexoro-updater-win-x64.exe"));
        WriteProject("[windows]\n" + UpdatesTable);

        Assert.Equal(ExitCodes.Failure, Run("pack"));

        Assert.Contains("zexoro-updater-win-x64.exe does not exist", Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Dist));
    }

    [Fact]
    public void Pack_WithUpdatesMissingKeyFile_ReportsThePosition()
    {
        PrepareUpdates();
        WriteProject("[windows]\n" + UpdatesTable.Replace("zexoro-update.pub", "missing.pub", StringComparison.Ordinal));

        Assert.Equal(ExitCodes.Failure, Run("pack"));

        Assert.Contains("zexoro.toml:17:9: updates.keys[0]: cannot read the key file", Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Dist));
    }

    [Fact]
    public void Pack_WithoutUpdates_WritesNoFragment()
    {
        WriteProject("[windows]\n");

        Assert.Equal(ExitCodes.Success, Run("pack"));

        Assert.Empty(Directory.GetFiles(Dist, "*.release.json"));
    }

    [Theory]
    [InlineData("--notes", "https://example.com/notes", "no [updates] table")]
    [InlineData("--channel", "beta", "--channel: there is no [updates] table")]
    public void Pack_UpdateOptionsWithoutUpdates_Fail(string option, string value, string message)
    {
        WriteProject("[windows]\n");

        Assert.Equal(ExitCodes.Failure, Run("pack", option, value));

        Assert.Contains(message, Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_NotesNotAUrl_Fails()
    {
        PrepareUpdates();
        WriteProject("[windows]\n" + UpdatesTable);

        Assert.Equal(ExitCodes.Failure, Run("pack", "--notes", "see the blog"));

        Assert.Contains("--notes: 'see the blog' is not an absolute http or https URL", Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_WithUpdates_ReportsTheFeedAndMissingUpdatersAsErrors()
    {
        PrepareUpdates();
        WriteProject("[windows]\n" + UpdatesTable);

        Run("doctor");

        var output = Stdout + Stderr;
        Assert.Contains("updates: channel stable, feed https://example.com/updates/stable/feed.json, 1 trusted key(s)", output, StringComparison.Ordinal);
        Assert.Matches("(?m)^error +updater: .*zexoro-updater-win-x86.exe does not exist", output);
    }

    [Fact]
    public void Pack_WithCertificateAndUpdates_SignsTheUpdater()
    {
        PrepareUpdates();
        using var certificate = TestCertificates.CreateSelfSigned(ecdsa: false);
        WriteFile("cert.pfx", certificate.Export(X509ContentType.Pkcs12, "pfx-pass"));
        _variables["CERT_PASSWORD"] = "pfx-pass";
        WriteProject("""
            [windows]

            [windows.signing]
            certificate = "cert.pfx"
            password = { env = "CERT_PASSWORD" }

            """ + UpdatesTable);

        Assert.Equal(ExitCodes.Success, Run("pack"));

        Assert.Contains("Signing zexoro-updater.exe", Stdout, StringComparison.Ordinal);

        // The staged copy, as it goes into the installer: signed with the application's certificate.
        var staged = PackKeepingStaging(withSigning: true);
        var updater = File.ReadAllBytes(Path.Combine(staged, NsisInstallerSpec.UpdaterFileName));
        Assert.True(AuthenticodeSigner.IsSigned(Path.Combine(staged, NsisInstallerSpec.UpdaterFileName)));
        var entry = TestPe.CertificateEntryOffset(pe32Plus: true);
        var offset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(updater.AsSpan(entry));
        var size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(updater.AsSpan(entry + 4));
        Assert.True(updater.AsSpan(offset, size).IndexOf(certificate.RawData) >= 0, "the signature does not carry the application's certificate");

        // The source in ZEXORO_UPDATER_DIR is left alone, and an unsigned build ships it unsigned.
        Assert.False(AuthenticodeSigner.IsSigned(Path.Combine(_dir.FullName, "updaters", "zexoro-updater-win-x64.exe")));
        var unsigned = PackKeepingStaging(withSigning: false);
        Assert.False(AuthenticodeSigner.IsSigned(Path.Combine(unsigned, NsisInstallerSpec.UpdaterFileName)));
    }

    /// <summary>Packs the current project with <see cref="NsisPackager"/> directly and returns the kept staging directory.</summary>
    private string PackKeepingStaging(bool withSigning)
    {
        var environment = new CliEnvironment(_dir.FullName, name => _variables.GetValueOrDefault(name)) { ZexoroCommand = DotnetZexoroCommand() };
        var config = Zexoro.Configuration.ZexoroConfig.Load(Path.Combine(_dir.FullName, "zexoro.toml"));
        using var credential = SigningCredential.LoadPfx(Path.Combine(_dir.FullName, "cert.pfx"), "pfx-pass");
        var result = NsisPackager.Pack(new NsisPackRequest
        {
            Config = config,
            Toolset = Toolsets.BundledToolsets.ResolveNsis(environment),
            OutputDirectory = Path.Combine(_dir.FullName, withSigning ? "signed" : "unsigned"),
            Signing = withSigning
                ? new NsisSigning
                {
                    Credential = credential,
                    CertificatePath = Path.Combine(_dir.FullName, "cert.pfx"),
                    Password = "pfx-pass",
                    ZexoroCommand = environment.ZexoroCommand,
                }
                : null,
            ResolveUpdater = architecture => Toolsets.BundledUpdaters.Resolve(architecture, environment),
            KeepStaging = true,
        });
        Assert.NotNull(result.StagingDirectory);
        _stagings.Add(result.StagingDirectory);
        return result.StagingDirectory;
    }
}
