using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Zexoro.Cli.Toolsets;
using Zexoro.Configuration;
using Zexoro.Signing.Fixtures;

namespace Zexoro.Cli.Tests;

public sealed class CommandTests : IDisposable
{
    private const string Config = """
        [app]
        id = "com.example.notes"
        name = "Example Notes"
        version = "1.2.3"
        publisher = "Example Ltd."
        icon = "assets/app.ico"

        [input]
        dir = "build/win-x64"
        main = "ExampleNotes.exe"

        [windows]

        """;

    private static readonly byte[] IcoHeader = [0, 0, 1, 0, 1, 0];

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-cli-");
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();
    private readonly Dictionary<string, string> _variables = [];

    public CommandTests()
    {
        // Keeps the doctor's toolset check from extracting into the user's real cache.
        _variables["ZEXORO_CACHE_DIR"] = Path.Combine(_dir.FullName, ".cache");

        // Test builds embed no updaters; the doctor's updater check finds these instead.
        _variables["ZEXORO_UPDATER_DIR"] = UpdaterFiles.Create(Path.Combine(_dir.FullName, ".updaters"));
    }

    public void Dispose()
    {
        _stdout.Dispose();
        _stderr.Dispose();
        _dir.Delete(recursive: true);
    }

    private string Stdout => _stdout.ToString();

    private string ConfigPath => Path.Combine(_dir.FullName, "zexoro.toml");

    private int Run(params string[] args) =>
        App.Run(args, _stdout, _stderr, new CliEnvironment(_dir.FullName, name => _variables.GetValueOrDefault(name)));

    private int Run(bool macOSHost, Func<string, IReadOnlyList<string>, Zexoro.Formats.MacOS.ToolResult> tools, params string[] args) =>
        App.Run(args, _stdout, _stderr, new CliEnvironment(_dir.FullName, name => _variables.GetValueOrDefault(name))
        {
            IsMacOSHost = macOSHost,
            MacTools = new DelegateTools(tools),
        });

    private sealed class DelegateTools(Func<string, IReadOnlyList<string>, Zexoro.Formats.MacOS.ToolResult> run) : Zexoro.Formats.MacOS.IMacToolRunner
    {
        public Zexoro.Formats.MacOS.ToolResult Run(string fileName, IReadOnlyList<string> arguments) => run(fileName, arguments);
    }

    private const string MacOSDoctorTables = """

        [macos]
        icon = "assets/app.png"

        [macos.signing]
        identity = "zexoro test signer"
        keychain = "/k/test.keychain-db"

        """;

    [Fact]
    public void Doctor_MacOSTarget_OnAnotherHost_IsAnError()
    {
        WriteProject();
        File.WriteAllText(ConfigPath, Config.Replace("main = \"ExampleNotes.exe\"", "main = \"ExampleNotes.exe\"\ntarget = \"osx-arm64\"", StringComparison.Ordinal) + MacOSDoctorTables);
        WriteFile("build/win-x64/ExampleNotes.exe", "#!/bin/sh\n"u8.ToArray());
        WriteFile("assets/app.png", Zexoro.Formats.Linux.PngIcon.FromBytes(TestIcon.Create()).Bytes);

        Assert.Equal(ExitCodes.Failure, Run(false, (_, _) => throw new InvalidOperationException("no tools here"), "doctor"));

        Assert.Contains("error    macOS bundles pack only on macOS", Stdout, StringComparison.Ordinal);
        Assert.Contains("ok       macos.icon:", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_MacOSTarget_ReportsToolsAndTheSigningIdentity()
    {
        WriteProject();
        File.WriteAllText(ConfigPath, Config.Replace("main = \"ExampleNotes.exe\"", "main = \"ExampleNotes.exe\"\ntarget = \"osx-arm64\"", StringComparison.Ordinal) + MacOSDoctorTables);
        WriteFile("build/win-x64/ExampleNotes.exe", "#!/bin/sh\n"u8.ToArray());
        WriteFile("assets/app.png", Zexoro.Formats.Linux.PngIcon.FromBytes(TestIcon.Create()).Bytes);
        var calls = new List<string>();

        Run(true, (file, arguments) =>
        {
            calls.Add(file + " " + string.Join(' ', arguments));
            return arguments switch
            {
                ["--find", var tool] => new Zexoro.Formats.MacOS.ToolResult(0, $"/Library/Developer/CommandLineTools/usr/bin/{tool}\n", string.Empty),
                ["find-identity", ..] => new Zexoro.Formats.MacOS.ToolResult(0, "  1) 1CB8651350F83737734D821809B0F1DC542B7432 \"zexoro test signer\" (CSSMERR_TP_NOT_TRUSTED)\n", string.Empty),
                _ => new Zexoro.Formats.MacOS.ToolResult(0, string.Empty, string.Empty),
            };
        }, "doctor");

        Assert.Contains("ok       codesign: /usr/bin/codesign", Stdout, StringComparison.Ordinal);
        Assert.Contains("ok       notarytool: /Library/Developer/CommandLineTools/usr/bin/notarytool", Stdout, StringComparison.Ordinal);
        Assert.Contains("ok       macos.signing: identity 'zexoro test signer' in /k/test.keychain-db", Stdout, StringComparison.Ordinal);
        Assert.Contains("/usr/bin/security find-identity -p codesigning /k/test.keychain-db", calls);
    }

    private void WriteFile(string relative, byte[] content)
    {
        var path = Path.Combine(_dir.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    /// <summary>Writes the configuration plus the files it names.</summary>
    private void WriteProject(string extra = "")
    {
        File.WriteAllText(ConfigPath, Config + extra);
        WriteFile("build/win-x64/ExampleNotes.exe", TestPe.Build(pe32Plus: true));
        WriteFile("assets/app.ico", IcoHeader);
    }

    private void WritePfx(string relative, string? password, DateTimeOffset notBefore, DateTimeOffset notAfter, bool codeSigning = true)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=zexoro doctor test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (codeSigning)
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.3")], false));
        }
        else
        {
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        }

        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        WriteFile(relative, certificate.Export(X509ContentType.Pfx, password));
    }

    private static string[] Lines(string text, string prefix) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    [Fact]
    public void Help_ListsCommands()
    {
        Assert.Equal(ExitCodes.Success, Run("--help"));
        Assert.Contains("init [--force]", Stdout, StringComparison.Ordinal);
        Assert.Contains("doctor [--config <path>]", Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("init", "--help")]
    [InlineData("doctor", "-h")]
    public void CommandHelp_PrintsUsage(params string[] args)
    {
        Assert.Equal(ExitCodes.Success, Run(args));
        Assert.Contains("Usage: zexoro", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_WritesTemplate()
    {
        Assert.Equal(ExitCodes.Success, Run("init"));

        var bytes = File.ReadAllBytes(ConfigPath);
        Assert.False(bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]), "no byte order mark");
        var text = File.ReadAllText(ConfigPath);
        Assert.Equal(ConfigTemplate.Text.ReplaceLineEndings("\n"), text);
        Assert.DoesNotContain('\r', text);
        Assert.Contains("Created", Stdout, StringComparison.Ordinal);
        Assert.Empty(_stderr.ToString());
    }

    [Fact]
    public void Init_TemplateLoadsAsConfiguration()
    {
        Run("init");

        var config = ZexoroConfig.Load(ConfigPath);

        Assert.Equal("com.example.myapp", config.App.Id);
        Assert.Equal(Path.Combine(_dir.FullName, "build", "win-x64", "MyApp.exe"), config.Input.Main.FullPath);
    }

    [Fact]
    public void Init_ExistingFile_RefusesToOverwrite()
    {
        File.WriteAllText(ConfigPath, "# mine\n");

        Assert.Equal(ExitCodes.Failure, Run("init"));

        Assert.Equal("# mine\n", File.ReadAllText(ConfigPath));
        Assert.Contains("already exists", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("--force", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Init_Force_Overwrites()
    {
        File.WriteAllText(ConfigPath, "# mine\n");

        Assert.Equal(ExitCodes.Success, Run("init", "--force"));

        Assert.Equal(ConfigTemplate.Text.ReplaceLineEndings("\n"), File.ReadAllText(ConfigPath));
    }

    [Theory]
    [InlineData("init", "--forse")]
    [InlineData("init", "extra")]
    [InlineData("doctor", "--config")]
    [InlineData("doctor", "--config=")]
    [InlineData("doctor", "--config", "a.toml", "--config", "b.toml")]
    [InlineData("doctor", "--verbose")]
    [InlineData("pakc")]
    public void InvalidArguments_FailWithUsageError(params string[] args)
    {
        Assert.Equal(ExitCodes.UsageError, Run(args));
        Assert.Empty(Stdout);
        Assert.Contains("zexoro --help", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_NoConfiguration_SuggestsInit()
    {
        Assert.Equal(ExitCodes.Failure, Run("doctor"));

        Assert.Contains("error    zexoro.toml: not found; run 'zexoro init'", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_FreshTemplate_ReportsMissingFiles()
    {
        Run("init");
        _stdout.GetStringBuilder().Clear();

        Assert.Equal(ExitCodes.Failure, Run("doctor"));

        Assert.Contains("ok       zexoro.toml: configuration is valid (com.example.myapp 0.1.0)", Stdout, StringComparison.Ordinal);
        Assert.Contains("error    zexoro.toml:14:8: input.dir: directory 'build/win-x64' not found", Stdout, StringComparison.Ordinal);
        Assert.Contains("error    zexoro.toml:9:13: app.icon: 'assets/app.ico' not found", Stdout, StringComparison.Ordinal);
        Assert.Contains("2 errors, 1 warning.", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_InvalidConfiguration_ListsEveryErrorWithPosition()
    {
        File.WriteAllText(ConfigPath, Config.Replace("\"1.2.3\"", "\"1.2\"", StringComparison.Ordinal) + "instal_scope = \"user\"\n");

        Assert.Equal(ExitCodes.Failure, Run("doctor"));

        var errors = Lines(Stdout, "error");
        Assert.Equal(2, errors.Length);
        Assert.StartsWith("error    zexoro.toml:4:11: app.version: '1.2' is not a Semantic Versioning 2.0.0 version", errors[0], StringComparison.Ordinal);
        Assert.Equal("error    zexoro.toml:13:1: unknown key 'windows.instal_scope'; did you mean 'install_scope'?", errors[1]);
        Assert.Empty(Lines(Stdout, "ok"));
    }

    [Fact]
    public void Doctor_ValidProjectWithoutSigning_SucceedsWithWarning()
    {
        WriteProject();

        Assert.Equal(ExitCodes.Success, Run("doctor"));

        Assert.Empty(Lines(Stdout, "error"));
        Assert.Equal(["warning  [windows.signing] is not configured; installers will be unsigned"], Lines(Stdout, "warning"));
        Assert.Equal(15, Lines(Stdout, "ok").Length); // configuration, input.dir, input.main, target, icon, NSIS toolset, 9 updaters
        Assert.Contains("ok       target win-x64 (input.main: a Windows x64 executable (PE)); formats: nsis", Stdout, StringComparison.Ordinal);
        Assert.Contains("0 errors, 1 warning.", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_ConfigOption_ResolvesAgainstWorkingDirectory()
    {
        WriteProject();
        Directory.CreateDirectory(Path.Combine(_dir.FullName, "conf"));
        File.Move(ConfigPath, Path.Combine(_dir.FullName, "conf", "app.toml"));

        Assert.Equal(ExitCodes.Failure, Run("doctor", "--config", "conf/app.toml"));
        Assert.Contains("conf/app.toml: configuration is valid", Stdout, StringComparison.Ordinal);
        Assert.Contains("conf/app.toml:9:7: input.dir", Stdout, StringComparison.Ordinal);

        _stdout.GetStringBuilder().Clear();
        File.Move(Path.Combine(_dir.FullName, "conf", "app.toml"), ConfigPath);
        Assert.Equal(ExitCodes.Success, Run("doctor", $"--config={ConfigPath}"));
    }

    [Fact]
    public void Doctor_ReportsTheUpdaters()
    {
        WriteProject();
        Assert.Equal(ExitCodes.Success, Run("doctor"));
        foreach (var runtime in Zexoro.Platforms.UpdaterRuntime.All)
        {
            Assert.Contains($"ok       updater for {runtime.Rid} from ZEXORO_UPDATER_DIR", Stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Doctor_WithoutUpdaterDirectory_WarnsOnlyWhenNoneAreEmbedded()
    {
        WriteProject();
        _variables.Remove("ZEXORO_UPDATER_DIR");
        Assert.Equal(ExitCodes.Success, Run("doctor"));

        // CI builds embed the updaters; local builds usually do not.
        const string Warning = "warning  this zexoro build has no embedded updaters; packages with [updates] need ZEXORO_UPDATER_DIR";
        if (BundledUpdaters.FindEmbedded(Zexoro.Platforms.Architecture.X64) is null)
        {
            Assert.Contains(Warning, Lines(Stdout, "warning"));
            Assert.Equal(2, Lines(Stdout, "warning").Length);
        }
        else
        {
            Assert.DoesNotContain(Warning, Lines(Stdout, "warning"));
            Assert.Single(Lines(Stdout, "warning"));
        }
    }

    [Fact]
    public void Doctor_IgnoresAMissingUpdaterForAnotherOperatingSystem()
    {
        WriteProject();
        File.Delete(Path.Combine(_variables["ZEXORO_UPDATER_DIR"], "zexoro-updater-linux-musl-x64"));
        Assert.Equal(ExitCodes.Success, Run("doctor"));
        Assert.DoesNotContain("linux-musl-x64", Stdout, StringComparison.Ordinal);
        Assert.Contains("ok       updater for linux-x64 from ZEXORO_UPDATER_DIR", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_ReportsAMissingUpdaterInTheOverrideDirectory()
    {
        WriteProject();
        File.Delete(Path.Combine(_variables["ZEXORO_UPDATER_DIR"], "zexoro-updater-win-arm64.exe"));
        Assert.Equal(ExitCodes.Success, Run("doctor"));
        Assert.Contains(Lines(Stdout, "warning"), l => l.Contains("zexoro-updater-win-arm64.exe does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void Doctor_NoWindowsTable_UsesTheWindowsDefaultsForAWindowsMain()
    {
        WriteProject();
        File.WriteAllText(ConfigPath, Config.Replace("[windows]", "", StringComparison.Ordinal));

        Assert.Equal(ExitCodes.Success, Run("doctor"));

        Assert.Contains("ok       target win-x64 (input.main: a Windows x64 executable (PE)); formats: nsis", Stdout, StringComparison.Ordinal);
        Assert.Contains("bundled NSIS", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_NoPlatformTableAndNoMain_Warns()
    {
        WriteProject();
        File.WriteAllText(ConfigPath, Config.Replace("[windows]", "", StringComparison.Ordinal));
        File.Delete(Path.Combine(_dir.FullName, "build", "win-x64", "ExampleNotes.exe"));

        Assert.Equal(ExitCodes.Failure, Run("doctor"));

        Assert.Contains("warning  the target platform is unknown until input.main exists", Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("NSIS", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_MainNotAnExecutable_IsAnError()
    {
        WriteProject();
        WriteFile("build/win-x64/ExampleNotes.exe", "#!/bin/sh\n"u8.ToArray());

        Assert.Equal(ExitCodes.Failure, Run("doctor"));

        Assert.Contains(Lines(Stdout, "error"), l => l.Contains("input.main:", StringComparison.Ordinal) && l.Contains("Set [input] target", StringComparison.Ordinal));
    }

    [Fact]
    public void Doctor_LinuxTarget_SkipsWindowsChecksAndReportsUnimplementedFormats()
    {
        WriteProject();
        File.WriteAllText(
            ConfigPath,
            Config.Replace("main = \"ExampleNotes.exe\"", "main = \"ExampleNotes.exe\"\ntarget = \"linux-x64\"", StringComparison.Ordinal) + "\n[linux]\nformats = [\"tar.gz\", \"deb\", \"rpm\", \"apk\", \"appimage\"]\n");
        WriteFile("build/win-x64/ExampleNotes.exe", "#!/bin/sh\n"u8.ToArray());
        WriteFile("assets/app.ico", TestIcon.Create());

        Assert.Equal(ExitCodes.Failure, Run("doctor"));

        Assert.Contains("error    the linux format 'appimage' is not implemented yet", Stdout, StringComparison.Ordinal);
        Assert.Contains("warning  [linux.rpm.signing] is not configured; rpm packages will be unsigned", Stdout, StringComparison.Ordinal);
        Assert.Contains("warning  [linux.apk.signing] is not configured; apk packages will be unsigned", Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("'apk' is not implemented", Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("'tar.gz' is not implemented", Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("'deb' is not implemented", Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("'rpm' is not implemented", Stdout, StringComparison.Ordinal);
        Assert.Contains("linux icon: converted from app.icon (16x16 PNG)", Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("NSIS", Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("windows.signing", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_MissingMainExecutable_IsAnError()
    {
        WriteProject();
        File.Delete(Path.Combine(_dir.FullName, "build", "win-x64", "ExampleNotes.exe"));

        Assert.Equal(ExitCodes.Failure, Run("doctor"));

        Assert.Contains("error    zexoro.toml:10:8: input.main: 'ExampleNotes.exe' not found in the input directory", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_MainWithoutExe_Warns()
    {
        WriteProject();
        File.WriteAllText(ConfigPath, Config.Replace("ExampleNotes.exe", "notes", StringComparison.Ordinal));
        WriteFile("build/win-x64/notes", TestPe.Build(pe32Plus: true));

        Assert.Equal(ExitCodes.Success, Run("doctor"));

        Assert.Contains("input.main: Windows installers expect an .exe entry point", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_IconNotIco_IsAnError()
    {
        WriteProject();
        WriteFile("assets/app.ico", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A]);

        Assert.Equal(ExitCodes.Failure, Run("doctor"));

        Assert.Contains("error    zexoro.toml:6:8: app.icon: 'assets/app.ico' is not an .ico file", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_License_IsChecked()
    {
        WriteProject("license = \"LICENSE.txt\"\n");

        Assert.Equal(ExitCodes.Failure, Run("doctor"));
        Assert.Contains("error    zexoro.toml:13:11: windows.license: 'LICENSE.txt' not found", Stdout, StringComparison.Ordinal);

        _stdout.GetStringBuilder().Clear();
        WriteFile("LICENSE.txt", [0x41]);
        Assert.Equal(ExitCodes.Success, Run("doctor"));
        Assert.Contains("ok       windows.license:", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_SigningCertificateLoads_ReportsOk()
    {
        WriteProject("""

            [windows.signing]
            certificate = { env = "SIGN_CERT_PATH" }
            password = { env = "SIGN_CERT_PASSWORD" }
            timestamp = "http://timestamp.digicert.com"
            """);
        WritePfx("certs/sign.pfx", "pa55", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        _variables["SIGN_CERT_PATH"] = "certs/sign.pfx";
        _variables["SIGN_CERT_PASSWORD"] = "pa55";

        Assert.Equal(ExitCodes.Success, Run("doctor"));

        Assert.Contains("ok       signing certificate 'CN=zexoro doctor test' loads", Stdout, StringComparison.Ordinal);
        Assert.Contains("ok       timestamp authority http://timestamp.digicert.com/ (not contacted)", Stdout, StringComparison.Ordinal);
        Assert.Contains("No problems found.", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_WrongPassword_IsAnError()
    {
        WriteProject("""

            [windows.signing]
            certificate = "sign.pfx"
            password = { env = "SIGN_CERT_PASSWORD" }
            timestamp = "http://timestamp.digicert.com"
            """);
        WritePfx("sign.pfx", "right", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        _variables["SIGN_CERT_PASSWORD"] = "wrong";

        Assert.Equal(ExitCodes.Failure, Run("doctor"));

        Assert.Contains("error    zexoro.toml:15:15: windows.signing.certificate: Cannot load the certificate file", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_UnsetEnvironmentVariable_IsAnError()
    {
        WriteProject("""

            [windows.signing]
            certificate = { env = "SIGN_CERT_PATH" }
            timestamp = { env = "SIGN_TSA" }
            """);

        Assert.Equal(ExitCodes.Failure, Run("doctor"));

        Assert.Contains("error    zexoro.toml:15:15: windows.signing.certificate: the environment variable 'SIGN_CERT_PATH' is not set", Stdout, StringComparison.Ordinal);
        Assert.Contains("error    zexoro.toml:16:13: windows.signing.timestamp: the environment variable 'SIGN_TSA' is not set", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_MissingCertificateFile_IsAnError()
    {
        WriteProject("\n[windows.signing]\ncertificate = \"missing.pfx\"\n");

        Assert.Equal(ExitCodes.Failure, Run("doctor"));

        Assert.Contains("windows.signing.certificate: certificate file", Stdout, StringComparison.Ordinal);
        Assert.Contains("missing.pfx not found", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_ExpiredCertificate_IsAnError()
    {
        WriteProject("\n[windows.signing]\ncertificate = \"old.pfx\"\ntimestamp = \"http://timestamp.digicert.com\"\n");
        WritePfx("old.pfx", null, DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Equal(ExitCodes.Failure, Run("doctor"));

        Assert.Contains("windows.signing.certificate: certificate 'CN=zexoro doctor test' expired on", Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Doctor_CertificateWarnings_AreReported()
    {
        WriteProject("\n[windows.signing]\ncertificate = \"tls.pfx\"\npassword = \"literal\"\n");
        WritePfx("tls.pfx", "literal", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10), codeSigning: false);

        Assert.Equal(ExitCodes.Success, Run("doctor"));

        var warnings = Lines(Stdout, "warning");
        Assert.Contains(warnings, w => w.Contains("windows.signing.password: the password is written in the configuration file", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("expires within 30 days", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("is not marked for code signing", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("windows.signing.timestamp is not set", StringComparison.Ordinal));
    }
}
