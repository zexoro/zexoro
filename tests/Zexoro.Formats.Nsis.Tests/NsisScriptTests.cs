using Zexoro.Configuration;
using Zexoro.Platforms;
using Zexoro.Versioning;

namespace Zexoro.Formats.Nsis.Tests;

public sealed class NsisScriptTests
{
    internal static NsisInstallerSpec UserDefault => new()
    {
        AppId = "com.example.notes",
        Name = "Example Notes",
        Version = SemanticVersion.Parse("1.2.3"),
        Publisher = "Example Ltd.",
        MainExecutable = "ExampleNotes.exe",
        Architecture = Architecture.X64,
        Scope = WindowsInstallScope.User,
        Shortcuts = WindowsShortcuts.StartMenu,
        EstimatedSizeKb = 1234,
    };

    internal static NsisInstallerSpec MachineFull => UserDefault with
    {
        Scope = WindowsInstallScope.Machine,
        Shortcuts = WindowsShortcuts.StartMenu | WindowsShortcuts.Desktop,
        LicenseFileName = "license.txt",
        Description = "Takes notes.",
        Url = new Uri("https://example.com/notes"),
        UninstallRemove = ["%LOCALAPPDATA%\\Example Notes", "%APPDATA%/Example Notes/settings.json", "%PROGRAMDATA%\\Example Notes\\cache"],
        EmptyDirectories = ["logs", "plugins/empty"],
        SignCommand = "sh ./sign.sh \"%1\"",
    };

    internal static NsisInstallerSpec UserEscaped => UserDefault with
    {
        Name = "Example $Notes & \"Co\"",
        Publisher = "${Publisher} $(lang)",
        Version = SemanticVersion.Parse("2.0.0-beta.1+build.7"),
        MainExecutable = "bin/My $App.exe",
        Architecture = Architecture.Arm64,
        Shortcuts = WindowsShortcuts.None,
        Description = "Line one\nLine two\t\"quoted\"",
        LicenseFileName = "license.rtf",
        SignCommand = "cmd.exe /d /c sign.cmd \"%1\"",
    };

    internal static NsisInstallerSpec MachineX86 => UserDefault with
    {
        Scope = WindowsInstallScope.Machine,
        Architecture = Architecture.X86,
        Shortcuts = WindowsShortcuts.Desktop,
    };

    [Fact]
    public void Generate_UserScopeDefaults_MatchesSnapshot() =>
        Snapshot.Match("user-default.nsi", NsisScript.Generate(UserDefault));

    [Fact]
    public void Generate_MachineScopeWithEverything_MatchesSnapshot() =>
        Snapshot.Match("machine-full.nsi", NsisScript.Generate(MachineFull));

    [Fact]
    public void Generate_EscapedValuesArm64Signed_MatchesSnapshot() =>
        Snapshot.Match("user-escaped-arm64.nsi", NsisScript.Generate(UserEscaped));

    [Fact]
    public void Generate_MachineScopeX86_MatchesSnapshot() =>
        Snapshot.Match("machine-x86.nsi", NsisScript.Generate(MachineX86));

    [Fact]
    public void Generate_UserScope_UsesHkcuWithoutElevation()
    {
        var script = NsisScript.Generate(UserDefault);
        Assert.Contains("RequestExecutionLevel user\n", script, StringComparison.Ordinal);
        Assert.Contains("!define ZX_REGROOT HKCU\n", script, StringComparison.Ordinal);
        Assert.Contains("InstallDir \"$LOCALAPPDATA\\Programs\\Example Notes\"", script, StringComparison.Ordinal);
        Assert.Contains("SetShellVarContext current", script, StringComparison.Ordinal);
        Assert.DoesNotContain("finalize", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$DESKTOP\\Example Notes.lnk\" \"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_MachineScope_UsesHklm64WithElevation()
    {
        var script = NsisScript.Generate(MachineFull);
        Assert.Contains("RequestExecutionLevel admin\n", script, StringComparison.Ordinal);
        Assert.Contains("!define ZX_REGROOT HKLM\n", script, StringComparison.Ordinal);
        Assert.Contains("InstallDir \"$PROGRAMFILES64\\Example Notes\"", script, StringComparison.Ordinal);
        Assert.Contains("SetRegView 64", script, StringComparison.Ordinal);
        Assert.Contains("UserInfo::GetAccountType", script, StringComparison.Ordinal);
        Assert.Contains("!uninstfinalize `sh ./sign.sh \"%1\"` = 0", script, StringComparison.Ordinal);
        Assert.Contains("!finalize `sh ./sign.sh \"%1\"` = 0", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_VersionResourceUsesSemVerCore()
    {
        var script = NsisScript.Generate(UserEscaped);
        Assert.Contains("VIProductVersion \"2.0.0.0\"", script, StringComparison.Ordinal);
        Assert.Contains("\"ProductVersion\" \"2.0.0-beta.1+build.7\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_UserStringsNeverEndALine()
    {
        var script = NsisScript.Generate(UserEscaped);
        Assert.DoesNotContain("Line one\n", script, StringComparison.Ordinal);
        Assert.Contains("Line one$\\nLine two$\\t$\\\"quoted$\\\"", script, StringComparison.Ordinal);
        Assert.Contains("Name \"Example $$Notes & $\\\"Co$\\\"\" \"Example $$Notes && $\\\"Co$\\\"\"", script, StringComparison.Ordinal);
        Assert.Contains("\"CompanyName\" \"$${Publisher} $$(lang)\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_CoreAbove65535_Throws()
    {
        var spec = UserDefault with { Version = SemanticVersion.Parse("1.70000.0") };
        var ex = Assert.Throws<NsisException>(() => NsisScript.Generate(spec));
        Assert.Contains("65535", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_TooLongValue_Throws()
    {
        var spec = UserDefault with { Description = new string('x', NsisScript.MaxStringLength + 1) };
        var ex = Assert.Throws<NsisException>(() => NsisScript.Generate(spec));
        Assert.Contains("app.description", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sign `x` \"%1\"")]
    [InlineData("sign ${X} \"%1\"")]
    [InlineData("sign $%PATH% \"%1\"")]
    public void Generate_UnsafeSignCommand_Throws(string command) =>
        Assert.Throws<ArgumentException>(() => NsisScript.Generate(UserDefault with { SignCommand = command }));

    [Theory]
    [InlineData("%LOCALAPPDATA%\\Example Notes", "current", "$LOCALAPPDATA\\Example Notes")]
    [InlineData("%localappdata%/Example/Sub/", "current", "$LOCALAPPDATA\\Example\\Sub")]
    [InlineData("%APPDATA%\\Example $Notes", "current", "$APPDATA\\Example $$Notes")]
    [InlineData("%PROGRAMDATA%\\Example", "all", "$APPDATA\\Example")]
    public void MapUninstallPath_MapsTokensAndContext(string path, string context, string expected) =>
        Assert.Equal((context, expected), NsisScript.MapUninstallPath(path));

    [Fact]
    public void MapUninstallPath_UnknownRoot_Throws() =>
        Assert.Throws<NsisException>(() => NsisScript.MapUninstallPath("C:\\Data"));
}
