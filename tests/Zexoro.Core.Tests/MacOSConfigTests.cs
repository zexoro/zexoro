using Zexoro.Configuration;

namespace Zexoro.Core.Tests;

/// <summary><c>[macos]</c>, <c>[macos.signing]</c> and <c>[macos.notarization]</c>.</summary>
public sealed class MacOSConfigTests
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

    private static readonly string Base = Path.GetTempPath();

    private static ZexoroConfig Parse(string text) => ZexoroConfig.Parse(Minimal + text, Base);

    private static List<string> Errors(string text) =>
        Assert.Throws<ConfigurationException>(() => Parse(text)).Errors.Select(e => e.Message).ToList();

    [Fact]
    public void MacOS_WithoutTable_HasDefaults()
    {
        var macOS = Parse(string.Empty).MacOS;

        Assert.Null(macOS.BundleId);
        Assert.Equal("com.example.notes", macOS.BundleIdentifier(Parse(string.Empty).App));
        Assert.Null(macOS.Icon);
        Assert.Null(macOS.MinimumVersion);
        Assert.Null(macOS.Category);
        Assert.Null(macOS.Signing);
        Assert.Null(macOS.Notarization);
    }

    [Fact]
    public void MacOS_ReadsEveryKey()
    {
        var config = Parse("""
            [macos]
            bundle_id = "com.example.Notes-App"
            icon = "assets/app.icns"
            minimum_version = "11.0"
            category = "public.app-category.developer-tools"

            [macos.signing]
            identity = "Developer ID Application: Example Ltd. (TEAMID1234)"
            keychain = { env = "MACOS_KEYCHAIN" }
            entitlements = "macos/entitlements.plist"
            installer_identity = "Developer ID Installer: Example Ltd. (TEAMID1234)"

            [macos.notarization]
            key = { env = "NOTARY_KEY_PATH" }
            key_id = { env = "NOTARY_KEY_ID" }
            issuer = "69a6de7e-0000-47e3-e053-5b8c7c11a4d1"
            """);
        var macOS = config.MacOS;

        Assert.Equal("com.example.Notes-App", macOS.BundleIdentifier(config.App));
        Assert.Equal(Path.Combine(Base, "assets", "app.icns"), macOS.Icon!.FullPath);
        Assert.Equal("11.0", macOS.MinimumVersion);
        Assert.Equal("public.app-category.developer-tools", macOS.Category);
        var signing = macOS.Signing!;
        Assert.Equal("Developer ID Application: Example Ltd. (TEAMID1234)", signing.Identity.Resolve());
        Assert.Equal("/k/build.keychain-db", signing.ResolveKeychain(name => name == "MACOS_KEYCHAIN" ? "/k/build.keychain-db" : null));
        Assert.Equal(Path.Combine(Base, "macos", "entitlements.plist"), signing.Entitlements!.FullPath);
        Assert.NotNull(signing.InstallerIdentity);
        var notarization = macOS.Notarization!;
        Assert.Equal(Path.Combine(Base, "keys", "AuthKey.p8"), notarization.ResolveKeyPath(_ => "keys/AuthKey.p8"));
        Assert.Equal("ABC", notarization.KeyId.Resolve(_ => "ABC"));
        Assert.Equal("69a6de7e-0000-47e3-e053-5b8c7c11a4d1", notarization.Issuer.Resolve());
    }

    [Fact]
    public void ResolveKeychain_KeepsANameThatIsNoFile()
    {
        var signing = Parse("[macos.signing]\nidentity = \"X\"\nkeychain = \"build.keychain\"\n").MacOS.Signing!;

        Assert.Equal("build.keychain", signing.ResolveKeychain());
    }

    [Theory]
    [InlineData("[macos]\nbundle_id = \"notes\"", "macos.bundle_id: 'notes' is not a reverse-DNS bundle identifier such as 'com.example.notes'")]
    [InlineData("[macos]\nbundle_id = \"com.example.notes_app\"", "macos.bundle_id: 'com.example.notes_app' may contain only ASCII letters, digits, '-' and '.'")]
    [InlineData("[macos]\nminimum_version = \"eleven\"", "macos.minimum_version: 'eleven' is not a macOS version such as '11.0' or '13.5'")]
    [InlineData("[macos]\nminimum_version = \"11.0.0.1\"", "macos.minimum_version: '11.0.0.1' is not a macOS version such as '11.0' or '13.5'")]
    [InlineData("[macos]\ncategory = \"productivity\"", "macos.category: 'productivity' is not an application category such as 'public.app-category.productivity'")]
    [InlineData("[macos.signing]\nkeychain = \"k\"", "[macos.signing]: missing required key 'identity'")]
    [InlineData("[macos.signing]\nidentity = \"-\"", "macos.signing.identity: '-' means ad-hoc signing, which zexoro does without [macos.signing]; name a certificate or remove the table")]
    [InlineData("[macos.signing]\nidentity = \"X\"\ntimestamp = true", "unknown key 'macos.signing.timestamp'")]
    [InlineData("[macos.notarization]\nkey = \"k.p8\"\nkey_id = \"A\"\nissuer = \"B\"", "[macos.notarization] needs [macos.signing]: Apple notarizes only code signed with a Developer ID certificate")]
    [InlineData("[macos.notarization]\nkey = \"k.p8\"", "[macos.notarization]: missing required key 'key_id'")]
    public void MacOS_InvalidValues_AreReported(string text, string expected)
    {
        Assert.Contains(expected, Errors(text));
    }
}
