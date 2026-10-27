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

    public PackCommandTests()
    {
        _variables[Zexoro.Toolsets.ToolsetCache.CacheDirectoryVariable] = Path.Combine(_dir.FullName, ".cache");
    }

    public void Dispose()
    {
        _stdout.Dispose();
        _stderr.Dispose();
        _dir.Delete(recursive: true);
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
    public void Pack_NoWindowsTable_Fails()
    {
        WriteProject(string.Empty);
        Assert.Equal(ExitCodes.Failure, Run("pack"));
        Assert.Contains("add a [windows] table", Stderr, StringComparison.Ordinal);
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
        Assert.Contains("is not a Windows executable", Stderr, StringComparison.Ordinal);
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
}
