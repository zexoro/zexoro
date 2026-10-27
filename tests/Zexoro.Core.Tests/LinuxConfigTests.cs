using Zexoro.Configuration;

namespace Zexoro.Core.Tests;

/// <summary><c>[linux]</c> keys and <see cref="LinuxPackageName"/>.</summary>
public sealed class LinuxConfigTests
{
    private const string Minimal = """
        [app]
        id = "com.example.Notes-App"
        name = "Example Notes"
        version = "1.2.3"
        publisher = "Example Ltd."
        icon = "assets/app.ico"

        [input]
        dir = "build"
        main = "notes"

        """;

    private static ZexoroConfig Parse(string text) => ZexoroConfig.Parse(text, Path.GetTempPath());

    private static IReadOnlyList<ConfigError> Errors(string text) =>
        Assert.Throws<ConfigurationException>(() => Parse(text)).Errors;

    [Fact]
    public void Linux_WithoutTable_HasDefaults()
    {
        var config = Parse(Minimal);

        Assert.Null(config.Linux.Package);
        Assert.Null(config.Linux.Icon);
        Assert.Empty(config.Linux.Categories);
        Assert.Equal("notes-app", config.Linux.PackageName(config.App));
        Assert.Null(config.Linux.Maintainer);
        Assert.Equal("Example Ltd.", config.Linux.MaintainerOf(config.App));
        Assert.Empty(config.Linux.Deb.Depends);
        Assert.Empty(config.Linux.Rpm.Requires);
        Assert.Null(config.Linux.Rpm.License);
        Assert.Null(config.Linux.Rpm.Signing);
        Assert.Empty(config.Linux.Apk.Depends);
        Assert.Null(config.Linux.Apk.License);
        Assert.Null(config.Linux.Apk.Signing);
    }

    [Fact]
    public void Linux_ApkKeys_AreRead()
    {
        var config = Parse(Minimal + """
            [linux.apk]
            depends = ["icu-libs", " so:libz.so.1 ", "foo>=1.2_rc1-r0", "!bar", "baz~1.2", "qux=~1"]
            license = "MIT"

            [linux.apk.signing]
            key = { env = "APK_KEY" }
            key_name = "dev@example.com-6123abcd.rsa.pub"
            """);

        Assert.Equal(["icu-libs", "so:libz.so.1", "foo>=1.2_rc1-r0", "!bar", "baz~1.2", "qux=~1"], config.Linux.Apk.Depends);
        Assert.Equal("MIT", config.Linux.Apk.License);
        var signing = Assert.IsType<ApkSigningConfig>(config.Linux.Apk.Signing);
        Assert.Equal("APK_KEY", signing.Key.EnvironmentVariable);
        Assert.Equal("dev@example.com-6123abcd.rsa.pub", signing.KeyName);
        var pem = "-----BEGIN PRIVATE KEY-----\nabc\n-----END PRIVATE KEY-----\n";
        Assert.Equal(pem, signing.ResolveKey(name => name == "APK_KEY" ? pem : null));
    }

    [Fact]
    public void ApkSigning_KeyFile_IsReadRelativeToTheConfiguration()
    {
        var directory = Directory.CreateTempSubdirectory("zexoro-apk-key-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "packager.rsa"), "pem");
            var config = ZexoroConfig.Parse(Minimal + "[linux.apk.signing]\nkey = \"packager.rsa\"\nkey_name = \"packager.rsa.pub\"\n", directory.FullName);
            var signing = config.Linux.Apk.Signing!;

            Assert.Equal("pem", signing.ResolveKey());
            File.Delete(Path.Combine(directory.FullName, "packager.rsa"));
            var error = Assert.Throws<ConfigurationException>(() => signing.ResolveKey());
            Assert.StartsWith("linux.apk.signing.key: the value is neither a PEM private key", Assert.Single(error.Errors).Message, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Linux_RpmKeys_AreRead()
    {
        var config = Parse(Minimal + """
            [linux.rpm]
            requires = ["libicu >= 60", " (a or b) "]
            license = "MIT"

            [linux.rpm.signing]
            key = { env = "RPM_KEY" }
            password = { env = "RPM_PASSWORD" }
            """);

        Assert.Equal(["libicu >= 60", "(a or b)"], config.Linux.Rpm.Requires);
        Assert.Equal("MIT", config.Linux.Rpm.License);
        var signing = Assert.IsType<RpmSigningConfig>(config.Linux.Rpm.Signing);
        Assert.Equal("RPM_KEY", signing.Key.EnvironmentVariable);
        var armored = "-----BEGIN PGP PRIVATE KEY BLOCK-----\n\nabc\n-----END PGP PRIVATE KEY BLOCK-----\n";
        Assert.Equal(armored, System.Text.Encoding.UTF8.GetString(signing.ResolveKey(name => name == "RPM_KEY" ? armored : null)));
        Assert.Equal("secret", signing.ResolvePassword(name => name == "RPM_PASSWORD" ? "secret" : null));
    }

    [Fact]
    public void RpmSigning_KeyFile_IsReadRelativeToTheConfiguration()
    {
        var directory = Directory.CreateTempSubdirectory("zexoro-rpm-key-");
        try
        {
            File.WriteAllBytes(Path.Combine(directory.FullName, "key.asc"), [1, 2, 3]);
            var config = ZexoroConfig.Parse(Minimal + "[linux.rpm.signing]\nkey = \"key.asc\"\n", directory.FullName);
            var signing = config.Linux.Rpm.Signing!;

            Assert.Equal([1, 2, 3], signing.ResolveKey());
            Assert.Null(signing.ResolvePassword());
            File.Delete(Path.Combine(directory.FullName, "key.asc"));
            var error = Assert.Throws<ConfigurationException>(() => signing.ResolveKey());
            Assert.StartsWith("linux.rpm.signing.key: the value is neither an armored OpenPGP key", Assert.Single(error.Errors).Message, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Linux_MaintainerAndDebDepends_AreRead()
    {
        var config = Parse(Minimal + """
            [linux]
            maintainer = "Example Ltd. <dev@example.com>"

            [linux.deb]
            depends = ["libicu74 | libicu72", "libfoo (>= 1.2)"]
            """);

        Assert.Equal("Example Ltd. <dev@example.com>", config.Linux.MaintainerOf(config.App));
        Assert.Equal(["libicu74 | libicu72", "libfoo (>= 1.2)"], config.Linux.Deb.Depends);
    }

    [Fact]
    public void Linux_Keys_AreRead()
    {
        var config = Parse(Minimal + """
            [linux]
            package = "notes2"
            icon = "assets/notes.png"
            categories = ["Office", "X-Example-Notes"]
            """);

        Assert.Equal("notes2", config.Linux.Package);
        Assert.Equal("notes2", config.Linux.PackageName(config.App));
        Assert.Equal("linux.icon", config.Linux.Icon?.Key);
        Assert.EndsWith(Path.Combine("assets", "notes.png"), config.Linux.Icon?.FullPath, StringComparison.Ordinal);
        Assert.Equal(["Office", "X-Example-Notes"], config.Linux.Categories);
    }

    [Theory]
    [InlineData("package = \"Notes\"", "linux.package: 'Notes' must start with a lower-case letter or a digit")]
    [InlineData("package = \"no_tes\"", "linux.package: 'no_tes' contains '_'; a package name may contain only lower-case letters, digits, '+', '-' and '.'")]
    [InlineData("package = \"n\"", "linux.package: 'n' is too short; a package name has at least two characters")]
    [InlineData("package = \"-notes\"", "linux.package: '-notes' must start with a lower-case letter or a digit")]
    [InlineData("categories = [\"Office;Utility\"]", "linux.categories[0]: 'Office;Utility' is not a desktop entry category (ASCII letters, digits and '-', such as 'Office' or 'Development')")]
    [InlineData("categories = [\"Office\", \"Office\"]", "linux.categories: 'Office' is listed twice")]
    [InlineData("categories = \"Office\"", "linux.categories must be an array, but is a string")]
    [InlineData("icon = \"\"", "linux.icon must not be empty")]
    [InlineData("maintainer = \"a\\nb\"", "linux.maintainer must be one line of text, such as 'Example Ltd. <dev@example.com>'")]
    [InlineData("maintainer = \" \"", "linux.maintainer must be one line of text, such as 'Example Ltd. <dev@example.com>'")]
    [InlineData("[linux.deb]\ndepends = [\"a\\nPackage: b\"]", "linux.deb.depends[0] must be one line of text, such as 'libicu74 | libicu72'")]
    [InlineData("[linux.deb]\ndepends = \"libc6\"", "linux.deb.depends must be an array, but is a string")]
    [InlineData("[linux.deb]\nrequires = []", "unknown key 'linux.deb.requires'")]
    [InlineData("[linux.rpm]\nrequires = [\"a\\nb\"]", "linux.rpm.requires[0] must be one line of text, such as 'libicu >= 60'")]
    [InlineData("[linux.rpm]\nlicense = \" \"", "linux.rpm.license must be one line of text, such as 'MIT' or 'LicenseRef-Proprietary'")]
    [InlineData("[linux.rpm.signing]\npassword = \"x\"", "[linux.rpm.signing]: missing required key 'key'")]
    [InlineData("[linux.apk]\ndepends = [\"a b\"]", "linux.apk.depends[0]: 'a b' is not one apk dependency: a name such as 'icu-libs' or 'so:libicuuc.so.74', optionally with an operator and a version such as 'icu-libs>=74', without spaces (list each dependency separately)")]
    [InlineData("[linux.apk]\ndepends = [\"foo>=\"]", "linux.apk.depends[0]: 'foo>=' is not one apk dependency: a name such as 'icu-libs' or 'so:libicuuc.so.74', optionally with an operator and a version such as 'icu-libs>=74', without spaces (list each dependency separately)")]
    [InlineData("[linux.apk]\ndepends = [\">=1\"]", "linux.apk.depends[0]: '>=1' is not one apk dependency: a name such as 'icu-libs' or 'so:libicuuc.so.74', optionally with an operator and a version such as 'icu-libs>=74', without spaces (list each dependency separately)")]
    [InlineData("[linux.apk]\nlicense = \" \"", "linux.apk.license must be one line of text, such as 'MIT' or 'LicenseRef-Proprietary'")]
    [InlineData("[linux.apk.signing]\nkey = \"k.rsa\"", "[linux.apk.signing]: missing required key 'key_name'")]
    [InlineData("[linux.apk.signing]\nkey = \"k.rsa\"\nkey_name = \"keys/k.rsa.pub\"", "linux.apk.signing.key_name: 'keys/k.rsa.pub' must be a file name of printable ASCII characters without spaces or '/', such as 'dev@example.com-6123abcd.rsa.pub'")]
    [InlineData("[linux.apk.signing]\nkey = \"k.rsa\"\nkey_name = \"..\"", "linux.apk.signing.key_name: '..' is not a file name; use the name of the public key file users install into /etc/apk/keys, such as 'dev@example.com-6123abcd.rsa.pub'")]
    [InlineData("[linux.apk.signing]\nkey = \"k.rsa\"\npassword = \"x\"\nkey_name = \"k.rsa.pub\"", "unknown key 'linux.apk.signing.password'")]
    public void Linux_InvalidKeys_AreErrors(string line, string message) =>
        Assert.Equal(message, Assert.Single(Errors(Minimal + "[linux]\n" + line + "\n")).Message);

    [Theory]
    [InlineData("com.example.notes", "notes")]
    [InlineData("com.example.Notes", "notes")]
    [InlineData("org.example.my-app2", "my-app2")]
    public void Derive_TakesTheLastSegmentLowerCased(string id, string expected) =>
        Assert.Equal(expected, LinuxPackageName.Derive(id));

    [Theory]
    [InlineData("notes")]
    [InlineData("0ad")]
    [InlineData("g++")]
    [InlineData("python3.12-notes")]
    public void Validate_ValidNames_AreAccepted(string name) =>
        Assert.Null(LinuxPackageName.Validate(name));

    [Fact]
    public void Validate_TooLong_IsRejected() =>
        Assert.NotNull(LinuxPackageName.Validate(new string('a', LinuxPackageName.MaxLength + 1)));
}
