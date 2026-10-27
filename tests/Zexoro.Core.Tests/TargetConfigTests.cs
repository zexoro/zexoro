using Zexoro.Configuration;
using Zexoro.Platforms;
using Zexoro.Signing.Fixtures;

namespace Zexoro.Core.Tests;

/// <summary><c>formats</c> in the platform tables, <c>[input] target</c>, the input overrides and target resolution.</summary>
public sealed class TargetConfigTests : IDisposable
{
    private const string Minimal = """
        [app]
        id = "com.example.notes"
        name = "Example Notes"
        version = "1.2.3"
        publisher = "Example Ltd."
        icon = "assets/app.ico"

        [input]
        dir = "build"
        main = "notes"

        """;

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-target-");

    public void Dispose() => _dir.Delete(recursive: true);

    private ZexoroConfig Parse(string text, ConfigOverrides? overrides = null) =>
        ZexoroConfig.Parse(text, _dir.FullName, "zexoro.toml", overrides);

    private IReadOnlyList<ConfigError> Errors(string text, ConfigOverrides? overrides = null) =>
        Assert.Throws<ConfigurationException>(() => Parse(text, overrides)).Errors;

    private void WriteMain(byte[] content, string name = "notes")
    {
        Directory.CreateDirectory(Path.Combine(_dir.FullName, "build"));
        File.WriteAllBytes(Path.Combine(_dir.FullName, "build", name), content);
    }

    [Fact]
    public void Formats_InEveryPlatformTable_AreReadInOrder()
    {
        var config = Parse(Minimal + """
            [windows]
            formats = ["zip", "nsis", "msi"]

            [linux]
            formats = ["deb", "tar.gz", "appimage", "rpm", "apk"]

            [macos]
            formats = ["pkg", "dmg", "zip"]
            """);

        Assert.Equal([PackageFormat.WindowsZip, PackageFormat.Nsis, PackageFormat.Msi], config.Windows.Formats);
        Assert.Equal([PackageFormat.Deb, PackageFormat.TarGz, PackageFormat.AppImage, PackageFormat.Rpm, PackageFormat.Apk], config.Linux.Formats);
        Assert.Equal([PackageFormat.Pkg, PackageFormat.Dmg, PackageFormat.MacOSZip], config.MacOS.Formats);
        Assert.True(config.Linux.IsConfigured);
        Assert.Same(config.MacOS, config.Platform(OperatingSystemFamily.MacOS));
        Assert.Null(config.Platform(OperatingSystemFamily.Android));
    }

    [Fact]
    public void Formats_EmptyPlatformTable_UsesTheDefault()
    {
        var config = Parse(Minimal + "[linux]\n[macos]\n");

        Assert.True(config.Linux.IsConfigured);
        Assert.Equal([PackageFormat.TarGz], config.Linux.Formats);
        Assert.Equal([PackageFormat.MacOSZip], config.MacOS.Formats);
    }

    [Theory]
    [InlineData("[windows]\nformats = [\"nsiss\"]", "windows.formats[0] must be 'nsis', 'zip' or 'msi', but is 'nsiss'; did you mean 'nsis'?")]
    [InlineData("[windows]\nformats = [\"deb\"]", "windows.formats[0] must be 'nsis', 'zip' or 'msi', but is 'deb'; 'deb' is a linux format, set it in [linux] formats")]
    [InlineData("[linux]\nformats = [\"dmg\"]", "linux.formats[0] must be 'tar.gz', 'appimage', 'deb', 'rpm' or 'apk', but is 'dmg'; 'dmg' is a macos format, set it in [macos] formats")]
    [InlineData("[macos]\nformats = [\"zip\", \"zip\"]", "macos.formats: 'zip' is listed twice")]
    [InlineData("[macos]\nformats = []", "macos.formats must list at least one format; omit it for the default (zip)")]
    [InlineData("[linux]\nformats = \"deb\"", "linux.formats must be an array, but is a string")]
    [InlineData("[linux]\nformats = [1]", "linux.formats[0] must be a string, but is an integer")]
    [InlineData("[linux]\nmaintainers = \"me\"", "unknown key 'linux.maintainers'; did you mean 'maintainer'?")]
    public void Formats_Invalid_IsAnError(string table, string message) =>
        Assert.Equal(message, Assert.Single(Errors(Minimal + table + "\n")).Message);

    [Fact]
    public void Target_IsRead()
    {
        var config = Parse(Minimal.Replace("main = \"notes\"", "main = \"notes\"\ntarget = \"osx-universal\"", StringComparison.Ordinal));

        Assert.Equal(new TargetPlatform(OperatingSystemFamily.MacOS, Architecture.Universal), config.Input.Target);
        Assert.Equal(11, config.Input.TargetPosition?.Line);
    }

    [Theory]
    [InlineData("linux-x46", "; did you mean 'linux-x64'?")]
    [InlineData("win-arm", "; did you mean 'win-arm64'?")]
    [InlineData("android-arm64", "")]
    [InlineData("linux-universal", "; did you mean 'osx-universal'?")]
    public void Target_Unsupported_IsAnError(string target, string suggestion)
    {
        var error = Assert.Single(Errors(Minimal.Replace("main = \"notes\"", $"main = \"notes\"\ntarget = \"{target}\"", StringComparison.Ordinal)));

        Assert.Equal(
            $"input.target must be 'win-x86', 'win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64' or 'osx-universal', but is '{target}'{suggestion}",
            error.Message);
    }

    [Fact]
    public void Overrides_ReplaceInputDirectoryAndMain()
    {
        var other = Path.Combine(_dir.FullName, "other");

        var config = Parse(Minimal, new ConfigOverrides { InputDirectory = other, Main = "bin\\app" });

        Assert.Equal(other, config.Input.Directory.FullPath);
        Assert.Equal("bin/app", config.Input.Main.Value);
        Assert.Equal(Path.Combine(other, "bin", "app"), config.Input.Main.FullPath);
    }

    [Theory]
    [InlineData("../app", "'../app' must not contain '.' or '..' segments")]
    [InlineData("/usr/bin/app", "'/usr/bin/app' must be a path relative to input.dir, not an absolute path")]
    public void Overrides_InvalidMain_IsAnError(string main, string message)
    {
        var error = Assert.Single(Errors(Minimal, new ConfigOverrides { Main = main }));

        Assert.Equal("--main", error.Source);
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void ResolveTarget_ReadsTheMainExecutable()
    {
        WriteMain(ExecutableInspectorTests.Elf(183));

        var target = Parse(Minimal).ResolveTarget();

        Assert.Equal(TargetPlatform.Parse("linux-arm64"), target.Platform);
        Assert.Equal("input.main: a Linux arm64 executable (ELF)", target.Origin);
    }

    [Fact]
    public void ResolveTarget_ScriptWithTarget_UsesTheTarget()
    {
        WriteMain("#!/bin/sh\n"u8.ToArray());

        var target = Parse(Minimal.Replace("main = \"notes\"", "main = \"notes\"\ntarget = \"linux-x64\"", StringComparison.Ordinal)).ResolveTarget();

        Assert.Equal(TargetPlatform.Parse("linux-x64"), target.Platform);
        Assert.Equal("input.target", target.Origin);
    }

    [Fact]
    public void ResolveTarget_TargetMatchingTheMain_IsAccepted()
    {
        WriteMain(TestPe.Build(pe32Plus: true));

        var target = Parse(Minimal.Replace("main = \"notes\"", "main = \"notes\"\ntarget = \"win-x64\"", StringComparison.Ordinal)).ResolveTarget();

        Assert.Equal(TargetPlatform.Parse("win-x64"), target.Platform);
    }

    [Fact]
    public void ResolveTarget_TargetContradictingTheMain_IsAnError()
    {
        WriteMain(TestPe.Build(pe32Plus: true));
        var config = Parse(Minimal.Replace("main = \"notes\"", "main = \"notes\"\ntarget = \"linux-x64\"", StringComparison.Ordinal));

        var error = Assert.Single(Assert.Throws<ConfigurationException>(() => config.ResolveTarget()).Errors);

        Assert.Equal("input.target: 'linux-x64' does not match input.main, which is a Windows x64 executable (PE) (win-x64)", error.Message);
        Assert.Equal(11, error.Position?.Line);
    }

    [Fact]
    public void ResolveTarget_UnknownMainWithoutTarget_AsksForTarget()
    {
        WriteMain("#!/bin/sh\n"u8.ToArray());
        var config = Parse(Minimal);

        var error = Assert.Single(Assert.Throws<ConfigurationException>(() => config.ResolveTarget()).Errors);

        Assert.StartsWith("input.main: ", error.Message, StringComparison.Ordinal);
        Assert.Contains("is not a PE, ELF or Mach-O executable", error.Message, StringComparison.Ordinal);
        Assert.Contains("Set [input] target", error.Message, StringComparison.Ordinal);
        Assert.Equal(10, error.Position?.Line);
    }

    [Fact]
    public void ResolveTarget_MissingInput_IsAnError()
    {
        var config = Parse(Minimal);
        Assert.Contains("build your application first", Assert.Throws<ConfigurationException>(() => config.ResolveTarget()).Errors[0].Message, StringComparison.Ordinal);

        Directory.CreateDirectory(Path.Combine(_dir.FullName, "build"));
        Assert.EndsWith("not found", Assert.Throws<ConfigurationException>(() => config.ResolveTarget()).Errors[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageFormat_Catalog_HasTheDesignedFormatsAndDefaults()
    {
        Assert.Equal(["nsis", "zip", "msi"], PackageFormat.NamesFor(OperatingSystemFamily.Windows));
        Assert.Equal(["tar.gz", "appimage", "deb", "rpm", "apk"], PackageFormat.NamesFor(OperatingSystemFamily.Linux));
        Assert.Equal(["zip", "dmg", "pkg"], PackageFormat.NamesFor(OperatingSystemFamily.MacOS));
        Assert.Empty(PackageFormat.For(OperatingSystemFamily.IOS));
        Assert.Empty(PackageFormat.DefaultFor(OperatingSystemFamily.Android));
        Assert.NotEqual(PackageFormat.WindowsZip, PackageFormat.MacOSZip);
        Assert.Equal([OperatingSystemFamily.Windows, OperatingSystemFamily.MacOS], PackageFormat.OperatingSystemsWith("zip"));
    }
}
