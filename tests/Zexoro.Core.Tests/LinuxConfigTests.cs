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
