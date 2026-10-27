using System.Text.RegularExpressions;
using Zexoro.Configuration;
using Zexoro.Toml;

namespace Zexoro.Core.Tests;

public class ZexoroConfigTests
{
    private const string Minimal = """
        [app]
        id = "com.example.notes"
        name = "Example Notes"
        version = "1.2.3"
        publisher = "Example Ltd."
        icon = "assets/app.ico"

        [input]
        dir = "build/win-x64"
        main = "ExampleNotes.exe"

        """;

    private static readonly string BaseDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "zexoro-config-tests"));

    private static ZexoroConfig Parse(string text, ConfigOverrides? overrides = null) =>
        ZexoroConfig.Parse(text, BaseDirectory, "zexoro.toml", overrides);

    private static IReadOnlyList<ConfigError> Errors(string text, ConfigOverrides? overrides = null) =>
        Assert.Throws<ConfigurationException>(() => Parse(text, overrides)).Errors;

    private static ConfigError SingleError(string text, ConfigOverrides? overrides = null) =>
        Assert.Single(Errors(text, overrides));

    private static string Full(string relative) => Path.GetFullPath(Path.Combine(BaseDirectory, relative));

    [Fact]
    public void Parse_Minimal_ReadsAppAndInput()
    {
        var config = Parse(Minimal);

        Assert.Equal("zexoro.toml", config.Source);
        Assert.Equal(BaseDirectory, config.BaseDirectory);
        Assert.Equal("com.example.notes", config.App.Id);
        Assert.Equal("Example Notes", config.App.Name);
        Assert.Equal("1.2.3", config.App.Version.ToString());
        Assert.Equal("Example Ltd.", config.App.Publisher);
        Assert.Equal("app.icon", config.App.Icon.Key);
        Assert.Equal("assets/app.ico", config.App.Icon.Value);
        Assert.Equal(Full("assets/app.ico"), config.App.Icon.FullPath);
        Assert.Equal(new TomlPosition(6, 8), config.App.Icon.Position);
        Assert.Null(config.App.Description);
        Assert.Null(config.App.Url);
        Assert.Equal(Full("build/win-x64"), config.Input.Directory.FullPath);
        Assert.Equal("ExampleNotes.exe", config.Input.Main.Value);
        Assert.Equal(Full("build/win-x64/ExampleNotes.exe"), config.Input.Main.FullPath);
        Assert.Null(config.Windows);
    }

    [Fact]
    public void Parse_ProductExample_ReadsEveryValue()
    {
        var config = Parse(Minimal + """

            [windows]
            install_scope = "machine"
            shortcuts = ["desktop", "start-menu"]
            license = "LICENSE.txt"
            sign = "all"

            [windows.signing]
            certificate = { env = "SIGN_CERT_PATH" }
            password = { env = "SIGN_CERT_PASSWORD" }
            timestamp = "http://timestamp.digicert.com"

            [windows.uninstall]
            remove = ['%LOCALAPPDATA%\Example Notes', "%appdata%/Example Notes/cache"]
            """);

        var windows = Assert.IsType<WindowsConfig>(config.Windows);
        Assert.Equal(WindowsInstallScope.Machine, windows.InstallScope);
        Assert.Equal(WindowsShortcuts.StartMenu | WindowsShortcuts.Desktop, windows.Shortcuts);
        Assert.Equal(Full("LICENSE.txt"), windows.License?.FullPath);
        Assert.Equal(WindowsSignFiles.All, windows.Sign);
        var signing = Assert.IsType<WindowsSigningConfig>(windows.Signing);
        Assert.Equal("SIGN_CERT_PATH", signing.Certificate.EnvironmentVariable);
        Assert.True(signing.Certificate.IsEnvironmentReference);
        Assert.Equal("SIGN_CERT_PASSWORD", signing.Password?.EnvironmentVariable);
        Assert.Equal("http://timestamp.digicert.com", signing.Timestamp?.Literal);
        Assert.Equal([@"%LOCALAPPDATA%\Example Notes", "%appdata%/Example Notes/cache"], windows.Uninstall.Remove);
    }

    [Fact]
    public void Parse_OptionalAppKeys_AreRead()
    {
        var config = Parse(Minimal.Replace("[input]", "description = \"Takes notes\"\nurl = \"https://example.com/notes\"\n\n[input]", StringComparison.Ordinal));

        Assert.Equal("Takes notes", config.App.Description);
        Assert.Equal(new Uri("https://example.com/notes"), config.App.Url);
    }

    [Fact]
    public void Parse_EmptyWindowsTable_UsesDefaults()
    {
        var windows = Assert.IsType<WindowsConfig>(Parse(Minimal + "\n[windows]\n").Windows);

        Assert.Equal(WindowsInstallScope.User, windows.InstallScope);
        Assert.Equal(WindowsShortcuts.StartMenu, windows.Shortcuts);
        Assert.Null(windows.License);
        Assert.Equal(WindowsSignFiles.Main, windows.Sign);
        Assert.Null(windows.Signing);
        Assert.Empty(windows.Uninstall.Remove);
    }

    [Fact]
    public void Parse_EmptyShortcuts_MeansNone()
    {
        var windows = Parse(Minimal + "\n[windows]\nshortcuts = []\n").Windows;

        Assert.Equal(WindowsShortcuts.None, windows?.Shortcuts);
    }

    [Fact]
    public void Parse_DottedKeysAndInlineTables_AreAccepted()
    {
        var config = Parse("""
            app = { id = "com.example.notes", name = "Notes", version = "1.0.0", publisher = "Me", icon = "a.ico" }
            input.dir = "out"
            input.main = 'bin\Notes.exe'
            """);

        Assert.Equal("Notes", config.App.Name);
        Assert.Equal("bin/Notes.exe", config.Input.Main.Value);
        Assert.Equal(Full("out/bin/Notes.exe"), config.Input.Main.FullPath);
    }

    [Fact]
    public void Parse_AbsolutePaths_AreKept()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "elsewhere", "app.ico");
        var config = Parse(Minimal.Replace("\"assets/app.ico\"", $"'{absolute}'", StringComparison.Ordinal));

        Assert.Equal(Path.GetFullPath(absolute), config.App.Icon.FullPath);
    }

    [Fact]
    public void Parse_Backslashes_AreDirectorySeparators()
    {
        var config = Parse(Minimal.Replace("\"build/win-x64\"", @"'build\win-x64'", StringComparison.Ordinal));

        Assert.Equal(Full("build/win-x64"), config.Input.Directory.FullPath);
    }

    [Fact]
    public void Parse_InvalidToml_ReportsParserPosition()
    {
        var error = SingleError("[app]\nid = \n");

        Assert.Equal("zexoro.toml", error.Source);
        Assert.Equal(new TomlPosition(2, 6), error.Position);
    }

    [Fact]
    public void Parse_EmptyDocument_ReportsMissingTablesWithoutPosition()
    {
        var errors = Errors("");

        Assert.Equal(["missing required table [app]", "missing required table [input]"], errors.Select(e => e.Message));
        Assert.All(errors, e => Assert.Null(e.Position));
    }

    [Theory]
    [InlineData("id", "[app]: missing required key 'id'")]
    [InlineData("name", "[app]: missing required key 'name'")]
    [InlineData("version", "[app]: missing required key 'version'")]
    [InlineData("publisher", "[app]: missing required key 'publisher'")]
    [InlineData("icon", "[app]: missing required key 'icon'")]
    public void Parse_MissingAppKey_PointsAtTableHeader(string key, string message)
    {
        var text = Regex.Replace(Minimal, $"(?m)^{key} = .*\n", "");

        var error = SingleError(text);

        Assert.Equal(message, error.Message);
        Assert.Equal(new TomlPosition(1, 1), error.Position);
    }

    [Fact]
    public void Parse_MissingInputKeys_PointAtTableHeader()
    {
        var errors = Errors(Minimal[..Minimal.IndexOf("dir", StringComparison.Ordinal)]);

        Assert.Equal(["[input]: missing required key 'dir'", "[input]: missing required key 'main'"], errors.Select(e => e.Message));
        Assert.All(errors, e => Assert.Equal(new TomlPosition(8, 1), e.Position));
    }

    [Theory]
    [InlineData("version = \"1.2.3\"", "version = 123", "app.version must be a string, but is an integer", 4, 11)]
    [InlineData("name = \"Example Notes\"", "name = true", "app.name must be a string, but is a boolean", 3, 8)]
    [InlineData("id = \"com.example.notes\"", "id = 1.5", "app.id must be a string, but is a float", 2, 6)]
    [InlineData("icon = \"assets/app.ico\"", "icon = [\"a.ico\"]", "app.icon must be a string, but is an array", 6, 8)]
    [InlineData("publisher = \"Example Ltd.\"", "publisher = 2026-10-26", "app.publisher must be a string, but is a local date", 5, 13)]
    [InlineData("main = \"ExampleNotes.exe\"", "main = { path = \"x\" }", "input.main must be a string, but is a table", 10, 8)]
    public void Parse_WrongType_PointsAtValue(string original, string replacement, string message, int line, int column)
    {
        var error = SingleError(Minimal.Replace(original, replacement, StringComparison.Ordinal));

        Assert.Equal(message, error.Message);
        Assert.Equal(new TomlPosition(line, column), error.Position);
    }

    [Fact]
    public void Parse_TableWithWrongType_PointsAtValue()
    {
        var errors = Errors("app = \"x\"\n[input]\ndir = \"d\"\nmain = \"m\"\n");

        var error = Assert.Single(errors);
        Assert.Equal("app must be a table, but is a string", error.Message);
        Assert.Equal(new TomlPosition(1, 7), error.Position);
    }

    [Fact]
    public void Parse_ArrayOfTables_IsRejected()
    {
        var error = SingleError(Minimal + "\n[[windows]]\n");

        Assert.Equal("windows must be a table, but is an array", error.Message);
    }

    [Theory]
    [InlineData("\nversoin = \"1\"", "unknown key 'app.versoin'; did you mean 'version'?")]
    [InlineData("\nNAME = \"x\"", "unknown key 'app.NAME'; did you mean 'name'?")]
    [InlineData("\nhomepage = \"x\"", "unknown key 'app.homepage'")]
    [InlineData("\ndesc = \"x\"", "unknown key 'app.desc'; did you mean 'description'?")]
    public void Parse_UnknownAppKey_PointsAtKey(string line, string message)
    {
        var error = SingleError(Minimal.Replace("\n\n[input]", line + "\n\n[input]", StringComparison.Ordinal));

        Assert.Equal(message, error.Message);
        Assert.Equal(new TomlPosition(7, 1), error.Position);
    }

    [Theory]
    [InlineData("[window]", "unknown table [window]; did you mean 'windows'?")]
    [InlineData("[macos]", "unknown table [macos]")]
    [InlineData("[app.extra]", "unknown table [app.extra]")]
    public void Parse_UnknownTable_IsAnError(string header, string message)
    {
        var error = SingleError(Minimal + "\n" + header + "\n");

        Assert.Equal(message, error.Message);
        Assert.Equal(12, error.Position?.Line);
    }

    [Theory]
    [InlineData("instal_scope = \"user\"", "unknown key 'windows.instal_scope'; did you mean 'install_scope'?", 1)]
    [InlineData("install-scope = \"user\"", "unknown key 'windows.install-scope'; did you mean 'install_scope'?", 1)]
    [InlineData("[windows.signin]\ncertificate = \"a\"", "unknown table [windows.signin]; did you mean 'signing'?", 10)]
    [InlineData("[windows.signing]\ncertificate = \"a\"\npasword = \"b\"", "unknown key 'windows.signing.pasword'; did you mean 'password'?", 1)]
    [InlineData("[windows.uninstall]\nremoves = []", "unknown key 'windows.uninstall.removes'; did you mean 'remove'?", 1)]
    [InlineData("[windows.signing]\ncertificate = { env = \"A\", default = \"b\" }", "unknown key 'windows.signing.certificate.default'", 28)]
    public void Parse_UnknownWindowsKey_IsAnError(string text, string message, int column)
    {
        var error = SingleError(Minimal + "\n[windows]\n" + text + "\n");

        Assert.Equal(message, error.Message);
        Assert.Equal(column, error.Position?.Column);
    }

    [Fact]
    public void Parse_UnknownRootKey_IsAnError()
    {
        var error = SingleError("title = \"x\"\n" + Minimal);

        Assert.Equal("unknown key 'title'", error.Message);
        Assert.Equal(new TomlPosition(1, 1), error.Position);
    }

    [Theory]
    [InlineData("notes")]
    [InlineData("com.")]
    [InlineData(".com.example")]
    [InlineData("com..example")]
    [InlineData("com.example.1notes")]
    [InlineData("com.example.notes-")]
    [InlineData("com.example.my_notes")]
    [InlineData("com.exämple.notes")]
    [InlineData("com example.notes")]
    public void Parse_IdNotReverseDns_IsAnError(string id)
    {
        var error = SingleError(Minimal.Replace("com.example.notes", id, StringComparison.Ordinal));

        Assert.StartsWith($"app.id: '{id}' is not a reverse-DNS identifier", error.Message, StringComparison.Ordinal);
        Assert.Equal(new TomlPosition(2, 6), error.Position);
    }

    [Theory]
    [InlineData("com.example")]
    [InlineData("io.github.some-user.Notes2")]
    [InlineData("A.B")]
    public void Parse_ReverseDnsId_IsAccepted(string id)
    {
        Assert.Equal(id, Parse(Minimal.Replace("com.example.notes", id, StringComparison.Ordinal)).App.Id);
    }

    [Theory]
    [InlineData("1.2", "expected MAJOR.MINOR.PATCH, such as 1.2.3")]
    [InlineData("v1.2.3", "the major version must be a non-negative integer")]
    [InlineData("1.02.3", "the minor version must not have leading zeros")]
    public void Parse_VersionNotSemVer_IsAnError(string version, string reason)
    {
        var error = SingleError(Minimal.Replace("\"1.2.3\"", $"\"{version}\"", StringComparison.Ordinal));

        Assert.Equal($"app.version: '{version}' is not a Semantic Versioning 2.0.0 version: {reason}", error.Message);
        Assert.Equal(new TomlPosition(4, 11), error.Position);
    }

    [Theory]
    [InlineData("", "app.name: must not be empty")]
    [InlineData(" Notes", "app.name: must not start or end with whitespace")]
    [InlineData("Notes.", "app.name: must not end with '.'")]
    [InlineData("A:B", "app.name: 'A:B' contains ':', which is not allowed in a directory name")]
    [InlineData("A\\tB", "app.name: 'A\tB' contains 'U+0009', which is not allowed in a directory name")]
    public void Parse_NameNotAFileName_IsAnError(string name, string message)
    {
        var error = SingleError(Minimal.Replace("\"Example Notes\"", $"\"{name}\"", StringComparison.Ordinal));

        Assert.Equal(message, error.Message);
        Assert.Equal(new TomlPosition(3, 8), error.Position);
    }

    [Fact]
    public void Parse_EmptyPublisher_IsAnError()
    {
        Assert.Equal("app.publisher must not be empty", SingleError(Minimal.Replace("\"Example Ltd.\"", "\" \"", StringComparison.Ordinal)).Message);
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("example.com")]
    [InlineData("/relative")]
    public void Parse_UrlNotHttp_IsAnError(string url)
    {
        var error = SingleError(Minimal.Replace("[input]", $"url = \"{url}\"\n[input]", StringComparison.Ordinal));

        Assert.Equal($"app.url: '{url}' is not an absolute http or https URL", error.Message);
        Assert.Equal(new TomlPosition(8, 7), error.Position);
    }

    [Theory]
    [InlineData("\"\"", "app.icon must not be empty")]
    [InlineData("\"  \"", "app.icon must not be empty")]
    [InlineData("\"a\\u0000b\"", "app.icon must not contain a NUL character")]
    public void Parse_InvalidPath_IsAnError(string value, string message)
    {
        var error = SingleError(Minimal.Replace("\"assets/app.ico\"", value, StringComparison.Ordinal));

        Assert.Equal(message, error.Message);
        Assert.Equal(new TomlPosition(6, 8), error.Position);
    }

    [Theory]
    [InlineData("\"\"", "input.main must name a file inside input.dir")]
    [InlineData("\"bin/\"", "input.main must name a file inside input.dir")]
    [InlineData("\"/usr/bin/app\"", "input.main must be a path relative to input.dir, not an absolute path")]
    [InlineData("'C:\\app.exe'", "input.main must be a path relative to input.dir, not an absolute path")]
    [InlineData("\"../app.exe\"", "input.main must not contain '.' or '..' segments")]
    [InlineData("\"./app.exe\"", "input.main must not contain '.' or '..' segments")]
    [InlineData("'bin\\..\\..\\app.exe'", "input.main must not contain '.' or '..' segments")]
    public void Parse_MainOutsideInputDir_IsAnError(string value, string message)
    {
        var error = SingleError(Minimal.Replace("\"ExampleNotes.exe\"", value, StringComparison.Ordinal));

        Assert.Equal(message, error.Message);
        Assert.Equal(new TomlPosition(10, 8), error.Position);
    }

    [Theory]
    [InlineData("install_scope = \"usr\"", "windows.install_scope must be 'user' or 'machine', but is 'usr'; did you mean 'user'?", 17)]
    [InlineData("install_scope = \"global\"", "windows.install_scope must be 'user' or 'machine', but is 'global'", 17)]
    [InlineData("install_scope = \"User\"", "windows.install_scope must be 'user' or 'machine', but is 'User'; did you mean 'user'?", 17)]
    [InlineData("sign = \"every\"", "windows.sign must be 'main' or 'all', but is 'every'", 8)]
    [InlineData("shortcuts = [\"start\"]", "windows.shortcuts must be 'start-menu' or 'desktop', but is 'start'; did you mean 'start-menu'?", 14)]
    [InlineData("shortcuts = [\"start_menu\"]", "windows.shortcuts must be 'start-menu' or 'desktop', but is 'start_menu'; did you mean 'start-menu'?", 14)]
    [InlineData("shortcuts = [\"desktop\", \"desktop\"]", "windows.shortcuts: 'desktop' is listed twice", 25)]
    [InlineData("shortcuts = [\"desktop\", 1]", "windows.shortcuts[1] must be a string, but is an integer", 25)]
    [InlineData("shortcuts = \"desktop\"", "windows.shortcuts must be an array, but is a string", 13)]
    [InlineData("license = 1", "windows.license must be a string, but is an integer", 11)]
    public void Parse_InvalidWindowsValue_IsAnError(string line, string message, int column)
    {
        var error = SingleError(Minimal + "\n[windows]\n" + line + "\n");

        Assert.Equal(message, error.Message);
        Assert.Equal(new TomlPosition(13, column), error.Position);
    }

    [Theory]
    [InlineData("password = \"x\"", "[windows.signing]: missing required key 'certificate'", 12, 1)]
    [InlineData("certificate = \"\"", "windows.signing.certificate must not be empty", 13, 15)]
    [InlineData("certificate = 42", "windows.signing.certificate must be a string or { env = \"NAME\" }, but is an integer", 13, 15)]
    [InlineData("certificate = {}", "[windows.signing.certificate]: missing required key 'env'", 13, 15)]
    [InlineData("certificate = { env = 1 }", "windows.signing.certificate.env must be a string, but is an integer", 13, 23)]
    [InlineData("certificate = { env = \"1PATH\" }", "windows.signing.certificate.env: '1PATH' is not a valid environment variable name (letters, digits and '_', not starting with a digit)", 13, 23)]
    [InlineData("certificate = { env = \"A-B\" }", "windows.signing.certificate.env: 'A-B' is not a valid environment variable name (letters, digits and '_', not starting with a digit)", 13, 23)]
    [InlineData("certificate = \"a.pfx\"\ntimestamp = \"timestamp.digicert.com\"", "windows.signing.timestamp: 'timestamp.digicert.com' is not an absolute http or https URL", 14, 13)]
    public void Parse_InvalidSigning_IsAnError(string lines, string message, int line, int column)
    {
        var error = SingleError(Minimal + "\n[windows.signing]\n" + lines + "\n");

        Assert.Equal(message, error.Message);
        Assert.Equal(new TomlPosition(line, column), error.Position);
    }

    [Theory]
    [InlineData("C:\\\\Users")]
    [InlineData("%TEMP%\\\\x")]
    [InlineData("%LOCALAPPDATA%")]
    [InlineData("%LOCALAPPDATA%\\\\")]
    [InlineData("Example Notes")]
    public void Parse_UninstallPathOutsideKnownFolders_IsAnError(string path)
    {
        var error = SingleError(Minimal + "\n[windows.uninstall]\nremove = [\"" + path + "\"]\n");

        Assert.Contains("must start with %LOCALAPPDATA%, %APPDATA%, %PROGRAMDATA% followed by a sub-path", error.Message, StringComparison.Ordinal);
        Assert.Equal(new TomlPosition(13, 11), error.Position);
    }

    [Theory]
    [InlineData("%APPDATA%/../Roaming")]
    [InlineData("%PROGRAMDATA%/Notes/./cache")]
    public void Parse_UninstallPathWithDotSegments_IsAnError(string path)
    {
        var error = SingleError(Minimal + "\n[windows.uninstall]\nremove = [\"" + path + "\"]\n");

        Assert.Equal($"windows.uninstall.remove: '{path}' must not contain '.' or '..' segments", error.Message);
    }

    [Fact]
    public void Parse_ManyErrors_AreAllReportedInDocumentOrder()
    {
        var errors = Errors("""
            [app]
            id = "notes"
            name = 1
            version = "1.2"
            publisher = "P"
            icon = "a.ico"
            colour = "red"

            [windows]
            install_scope = "everyone"
            """);

        string[] expected =
        [
            "app.id: 'notes' is not a reverse-DNS identifier",
            "app.name must be a string, but is an integer",
            "app.version: '1.2' is not a Semantic Versioning 2.0.0 version",
            "unknown key 'app.colour'",
            "windows.install_scope must be 'user' or 'machine'",
            "missing required table [input]",
        ];
        Assert.Equal(expected.Length, errors.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.StartsWith(expected[i], errors[i].Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ConfigurationException_MessageListsEveryError()
    {
        var ex = Assert.Throws<ConfigurationException>(() => Parse(""));

        Assert.Contains("2 errors", ex.Message, StringComparison.Ordinal);
        Assert.Contains("zexoro.toml: missing required table [app]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("zexoro.toml: missing required table [input]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfigError_ToString_IncludesSourceAndPosition()
    {
        Assert.Equal("a.toml:3:7: bad", new ConfigError("bad", "a.toml", new TomlPosition(3, 7)).ToString());
        Assert.Equal("--version: bad", new ConfigError("bad", "--version", null).ToString());
    }

    [Fact]
    public void Parse_VersionOverride_ReplacesFileVersion()
    {
        var config = Parse(Minimal, new ConfigOverrides { Version = "2.0.0-rc.1+ci.42" });

        Assert.Equal("2.0.0-rc.1+ci.42", config.App.Version.ToString());
    }

    [Fact]
    public void Parse_VersionOverride_MakesFileVersionOptionalAndUnchecked()
    {
        var overrides = new ConfigOverrides { Version = "3.1.4" };

        Assert.Equal("3.1.4", Parse(Regex.Replace(Minimal, "(?m)^version = .*\n", ""), overrides).App.Version.ToString());
        Assert.Equal("3.1.4", Parse(Minimal.Replace("\"1.2.3\"", "\"dev\"", StringComparison.Ordinal), overrides).App.Version.ToString());
    }

    [Fact]
    public void Parse_InvalidVersionOverride_IsAnErrorWithoutPosition()
    {
        var error = SingleError(Minimal, new ConfigOverrides { Version = "1.0" });

        Assert.Equal("--version", error.Source);
        Assert.Null(error.Position);
        Assert.Equal("'1.0' is not a Semantic Versioning 2.0.0 version: expected MAJOR.MINOR.PATCH, such as 1.2.3", error.Message);
    }

    [Fact]
    public void Parse_InvalidVersionOverride_IsReportedEvenWithoutAppTable()
    {
        var errors = Errors("", new ConfigOverrides { Version = "x" });

        Assert.Contains(errors, e => e.Source == "--version");
    }

    [Fact]
    public void Load_ReadsFileAndResolvesPathsAgainstItsDirectory()
    {
        var dir = Directory.CreateTempSubdirectory("zexoro-config-");
        try
        {
            var path = Path.Combine(dir.FullName, "sub", "zexoro.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Minimal);

            var config = ZexoroConfig.Load(path);

            Assert.Equal(path, config.Source);
            Assert.Equal(Path.Combine(dir.FullName, "sub"), config.BaseDirectory);
            Assert.Equal(Path.Combine(dir.FullName, "sub", "assets", "app.ico"), config.App.Icon.FullPath);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_UsesGivenSourceInErrors()
    {
        var dir = Directory.CreateTempSubdirectory("zexoro-config-");
        try
        {
            var path = Path.Combine(dir.FullName, "zexoro.toml");
            File.WriteAllText(path, "[app\n");

            var error = Assert.Single(Assert.Throws<ConfigurationException>(() => ZexoroConfig.Load(path, source: "my.toml")).Errors);

            Assert.Equal("my.toml", error.Source);
            Assert.Equal(1, error.Position?.Line);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_MissingFile_IsAnErrorWithoutPosition()
    {
        var path = Path.Combine(Path.GetTempPath(), $"zexoro-missing-{Guid.NewGuid():N}.toml");

        var error = Assert.Single(Assert.Throws<ConfigurationException>(() => ZexoroConfig.Load(path)).Errors);

        Assert.Equal(path, error.Source);
        Assert.Null(error.Position);
        Assert.StartsWith("cannot read the configuration file", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Template_IsAValidConfiguration()
    {
        var config = Parse(ConfigTemplate.Text);

        Assert.Equal("com.example.myapp", config.App.Id);
        Assert.Equal("0.1.0", config.App.Version.ToString());
        Assert.Equal(WindowsInstallScope.User, config.Windows?.InstallScope);
        Assert.Equal(WindowsShortcuts.StartMenu, config.Windows?.Shortcuts);
        Assert.Null(config.Windows?.Signing);
    }

    [Fact]
    public void Template_WithEveryCommentedSettingEnabled_IsAValidConfiguration()
    {
        var enabled = Regex.Replace(ConfigTemplate.Text, @"(?m)^# (?=\[|[a-z_]+\s*=)", "");
        Assert.NotEqual(ConfigTemplate.Text, enabled);

        var config = Parse(enabled);

        Assert.Equal("What the application does", config.App.Description);
        Assert.Equal(new Uri("https://example.com"), config.App.Url);
        var windows = Assert.IsType<WindowsConfig>(config.Windows);
        Assert.Equal(Full("LICENSE.txt"), windows.License?.FullPath);
        Assert.Equal("SIGN_CERT_PATH", windows.Signing?.Certificate.EnvironmentVariable);
        Assert.Equal("SIGN_CERT_PASSWORD", windows.Signing?.Password?.EnvironmentVariable);
        Assert.Equal("http://timestamp.digicert.com", windows.Signing?.Timestamp?.Literal);
        Assert.Equal([@"%LOCALAPPDATA%\My App"], windows.Uninstall.Remove);
    }
}
