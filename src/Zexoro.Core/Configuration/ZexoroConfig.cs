using Zexoro.Platforms;
using Zexoro.Toml;
using Zexoro.Versioning;

namespace Zexoro.Configuration;

/// <summary>
/// A validated <c>zexoro.toml</c>. Each top-level table maps to one property; platform tables
/// (<c>[windows]</c>, <c>[linux]</c>, <c>[macos]</c>) are optional and take their defaults when
/// absent, and new platforms, formats or update settings are added as further tables without
/// changing the existing ones.
/// </summary>
public sealed class ZexoroConfig
{
    /// <summary>The configuration file name that <c>zexoro init</c> writes and the CLI looks for.</summary>
    public const string DefaultFileName = "zexoro.toml";

    internal ZexoroConfig(
        string source,
        string baseDirectory,
        AppConfig app,
        InputConfig input,
        WindowsConfig windows,
        LinuxConfig linux,
        MacOSConfig macOS,
        UpdatesConfig? updates)
    {
        Updates = updates;
        Source = source;
        BaseDirectory = baseDirectory;
        App = app;
        Input = input;
        Windows = windows;
        Linux = linux;
        MacOS = macOS;
    }

    /// <summary>The configuration file path as given, used in messages.</summary>
    public string Source { get; }

    /// <summary>The absolute directory that relative paths are resolved against: the configuration file's directory.</summary>
    public string BaseDirectory { get; }

    /// <summary><c>[app]</c>: what the application is.</summary>
    public AppConfig App { get; }

    /// <summary><c>[input]</c>: the build output to package.</summary>
    public InputConfig Input { get; }

    /// <summary><c>[windows]</c>; the defaults when the table is absent (see <see cref="PlatformConfig.IsConfigured"/>).</summary>
    public WindowsConfig Windows { get; }

    /// <summary><c>[linux]</c>; the defaults when the table is absent.</summary>
    public LinuxConfig Linux { get; }

    /// <summary><c>[macos]</c>; the defaults when the table is absent.</summary>
    public MacOSConfig MacOS { get; }

    /// <summary>The platform table of <paramref name="operatingSystem"/>, or null for one zexoro does not package for.</summary>
    public PlatformConfig? Platform(OperatingSystemFamily operatingSystem)
    {
        if (operatingSystem == OperatingSystemFamily.Windows)
        {
            return Windows;
        }

        if (operatingSystem == OperatingSystemFamily.Linux)
        {
            return Linux;
        }

        return operatingSystem == OperatingSystemFamily.MacOS ? MacOS : null;
    }

    /// <summary>
    /// Determines the target platform of this build output: <c>[input] target</c> when set, otherwise
    /// the platform of the main executable's header (docs/design/0006-desktop-formats.md section 4).
    /// </summary>
    /// <exception cref="ConfigurationException">
    /// The input directory or main file does not exist, the main file is not a supported executable
    /// and no target is set, or the set target contradicts the main executable.
    /// </exception>
    public PackTarget ResolveTarget()
    {
        var input = Input;
        if (!Directory.Exists(input.Directory.FullPath))
        {
            throw Error(input.Directory, $"{input.Directory.Key}: directory {input.Directory.FullPath} not found; build your application first");
        }

        if (!File.Exists(input.Main.FullPath))
        {
            throw Error(input.Main, $"{input.Main.Key}: {input.Main.FullPath} not found");
        }

        ExecutableInfo executable;
        try
        {
            executable = ExecutableInspector.Inspect(input.Main.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Error(input.Main, $"{input.Main.Key}: cannot read {input.Main.FullPath}: {ex.Message}");
        }

        if (input.Target is { } target)
        {
            if (executable.Target is { } detected && detected != target)
            {
                throw new ConfigurationException([new ConfigError(
                    $"input.target: '{target}' does not match {input.Main.Key}, which is {executable.Description} ({detected})",
                    Source,
                    input.TargetPosition)]);
            }

            return new PackTarget(target, "input.target");
        }

        return executable.Target is { } found
            ? new PackTarget(found, $"{input.Main.Key}: {executable.Description}")
            : throw Error(input.Main, $"{input.Main.Key}: {input.Main.FullPath} is {executable.Description}, so the target platform is unknown; "
                + $"zexoro packages {string.Join(", ", PackagingTargets.All)}. Set [input] target for an entry point that is not a native executable, such as a script");
    }

    private ConfigurationException Error(ConfigPath path, string message) =>
        new([new ConfigError(message, Source, path.Position)]);

    /// <summary><c>[updates]</c>, or null when the table is absent (installers without an updater).</summary>
    public UpdatesConfig? Updates { get; }

    /// <summary>Reads and validates a configuration file.</summary>
    /// <param name="path">The file to read.</param>
    /// <param name="overrides">Command-line values that replace configuration values.</param>
    /// <param name="source">The name used in messages; defaults to <paramref name="path"/>.</param>
    /// <exception cref="ConfigurationException">The file cannot be read or is not valid; holds every error found.</exception>
    public static ZexoroConfig Load(string path, ConfigOverrides? overrides = null, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        source ??= path;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigurationException([new ConfigError($"cannot read the configuration file: {ex.Message}", source, null)]);
        }

        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        TomlTable root;
        try
        {
            root = TomlParser.Parse(bytes);
        }
        catch (TomlException ex)
        {
            throw new ConfigurationException([new ConfigError(ex.Reason, source, ex.Position)]);
        }

        return ConfigReader.Read(root, source, baseDirectory, overrides);
    }

    /// <summary>Parses and validates configuration text.</summary>
    /// <param name="text">The TOML text.</param>
    /// <param name="baseDirectory">The directory relative paths are resolved against.</param>
    /// <param name="source">The name used in error messages.</param>
    /// <param name="overrides">Command-line values that replace configuration values.</param>
    /// <exception cref="ConfigurationException">The text is not valid; holds every error found.</exception>
    public static ZexoroConfig Parse(string text, string baseDirectory, string source = DefaultFileName, ConfigOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(baseDirectory);
        ArgumentNullException.ThrowIfNull(source);
        TomlTable root;
        try
        {
            root = TomlParser.Parse(text);
        }
        catch (TomlException ex)
        {
            throw new ConfigurationException([new ConfigError(ex.Reason, source, ex.Position)]);
        }

        return ConfigReader.Read(root, source, Path.GetFullPath(baseDirectory), overrides);
    }
}

/// <summary><c>[app]</c>: the application's identity and metadata, shared by every platform.</summary>
public sealed class AppConfig
{
    internal AppConfig(string id, string name, SemanticVersion version, string publisher, ConfigPath icon, string? description, Uri? url)
    {
        Id = id;
        Name = name;
        Version = version;
        Publisher = publisher;
        Icon = icon;
        Description = description;
        Url = url;
    }

    /// <summary>The permanent reverse-DNS identity, e.g. <c>com.example.notes</c>.</summary>
    public string Id { get; }

    /// <summary>The display name; also used as the install directory name, so it is a valid file name.</summary>
    public string Name { get; }

    /// <summary>The version, from the file or <c>--version</c>.</summary>
    public SemanticVersion Version { get; }

    public string Publisher { get; }

    /// <summary>The application icon.</summary>
    public ConfigPath Icon { get; }

    public string? Description { get; }

    /// <summary>The application's web site (absolute http or https).</summary>
    public Uri? Url { get; }
}

/// <summary>The target platform of a build output and where it came from.</summary>
/// <param name="Platform">The target platform.</param>
/// <param name="Origin">How it was determined, for messages: <c>input.target</c>, or <c>input.main: &lt;what the header says&gt;</c>.</param>
public sealed record PackTarget(TargetPlatform Platform, string Origin);

/// <summary><c>[input]</c>: the already-built application directory.</summary>
public sealed class InputConfig
{
    internal InputConfig(ConfigPath directory, ConfigPath main, TargetPlatform? target, TomlPosition? targetPosition)
    {
        Directory = directory;
        Main = main;
        Target = target;
        TargetPosition = targetPosition;
    }

    /// <summary>The build output directory.</summary>
    public ConfigPath Directory { get; }

    /// <summary>
    /// The entry point. <see cref="ConfigPath.Value"/> is relative to <see cref="Directory"/> (as
    /// written, with forward slashes); <see cref="ConfigPath.FullPath"/> is absolute.
    /// </summary>
    public ConfigPath Main { get; }

    /// <summary>
    /// <c>target</c>: the target platform when the main file is not a native executable (a
    /// script), or null to read it from the main executable.
    /// </summary>
    public TargetPlatform? Target { get; }

    /// <summary>Where <see cref="Target"/> is written, or null when it is not set.</summary>
    public TomlPosition? TargetPosition { get; }
}

/// <summary>What every platform table (<c>[windows]</c>, <c>[linux]</c>, <c>[macos]</c>) has.</summary>
public abstract class PlatformConfig
{
    private protected PlatformConfig(OperatingSystemFamily operatingSystem, bool isConfigured, IReadOnlyList<PackageFormat> formats)
    {
        OperatingSystem = operatingSystem;
        IsConfigured = isConfigured;
        Formats = formats;
    }

    /// <summary>The operating system the table configures.</summary>
    public OperatingSystemFamily OperatingSystem { get; }

    /// <summary>Whether the table is written in the file; when not, every value is its default.</summary>
    public bool IsConfigured { get; }

    /// <summary><c>formats</c>: the formats to build for this operating system, in order, without duplicates.</summary>
    public IReadOnlyList<PackageFormat> Formats { get; }
}

/// <summary>Who a Windows install is for.</summary>
public enum WindowsInstallScope
{
    /// <summary>The current user, under <c>%LOCALAPPDATA%\Programs</c>, without elevation.</summary>
    User,

    /// <summary>All users, under <c>%ProgramFiles%</c>, with elevation.</summary>
    Machine,
}

/// <summary>Shortcuts a Windows installer creates.</summary>
[Flags]
public enum WindowsShortcuts
{
    None = 0,
    StartMenu = 1,
    Desktop = 2,
}

/// <summary>Which application files are Authenticode-signed when a certificate is configured.</summary>
public enum WindowsSignFiles
{
    /// <summary>Only the main executable.</summary>
    Main,

    /// <summary>Every PE file (exe, dll) in the input directory that is not already signed.</summary>
    All,
}

/// <summary><c>[windows]</c>: Windows installer settings.</summary>
public sealed class WindowsConfig : PlatformConfig
{
    internal WindowsConfig(
        bool isConfigured,
        IReadOnlyList<PackageFormat> formats,
        WindowsInstallScope installScope,
        WindowsShortcuts shortcuts,
        ConfigPath? license,
        WindowsSignFiles sign,
        WindowsSigningConfig? signing,
        WindowsUninstallConfig uninstall)
        : base(OperatingSystemFamily.Windows, isConfigured, formats)
    {
        InstallScope = installScope;
        Shortcuts = shortcuts;
        License = license;
        Sign = sign;
        Signing = signing;
        Uninstall = uninstall;
    }

    /// <summary><c>install_scope</c>; defaults to <see cref="WindowsInstallScope.User"/>.</summary>
    public WindowsInstallScope InstallScope { get; }

    /// <summary><c>shortcuts</c>; defaults to the Start menu only.</summary>
    public WindowsShortcuts Shortcuts { get; }

    /// <summary><c>license</c>: a license file shown on an installer page, or null for no page.</summary>
    public ConfigPath? License { get; }

    /// <summary><c>sign</c>; defaults to <see cref="WindowsSignFiles.Main"/>.</summary>
    public WindowsSignFiles Sign { get; }

    /// <summary><c>[windows.signing]</c>, or null to skip signing.</summary>
    public WindowsSigningConfig? Signing { get; }

    /// <summary><c>[windows.uninstall]</c>; empty when absent.</summary>
    public WindowsUninstallConfig Uninstall { get; }
}

/// <summary><c>[linux]</c>: Linux package settings.</summary>
public sealed class LinuxConfig : PlatformConfig
{
    internal LinuxConfig(
        bool isConfigured,
        IReadOnlyList<PackageFormat> formats,
        string? package,
        ConfigPath? icon,
        IReadOnlyList<string> categories,
        string? maintainer,
        LinuxDebConfig deb,
        LinuxRpmConfig rpm,
        LinuxApkConfig apk)
        : base(OperatingSystemFamily.Linux, isConfigured, formats)
    {
        Rpm = rpm;
        Apk = apk;
        Package = package;
        Icon = icon;
        Categories = categories;
        Maintainer = maintainer;
        Deb = deb;
    }

    /// <summary>
    /// <c>package</c>: the package name of deb, rpm and apk packages, already validated
    /// (<see cref="LinuxPackageName"/>), or null for the default derived from <c>app.id</c>.
    /// </summary>
    public string? Package { get; }

    /// <summary><c>icon</c>: a PNG icon, or null to convert the largest image of <c>[app] icon</c>.</summary>
    public ConfigPath? Icon { get; }

    /// <summary><c>categories</c>: the <c>Categories</c> of the <c>.desktop</c> file, in order; empty when not set.</summary>
    public IReadOnlyList<string> Categories { get; }

    /// <summary>
    /// <c>maintainer</c>: the maintainer of deb, rpm and apk packages (one line, conventionally
    /// <c>Name &lt;email&gt;</c>), or null for <c>[app] publisher</c>.
    /// </summary>
    public string? Maintainer { get; }

    /// <summary><c>[linux.deb]</c>; empty when absent.</summary>
    public LinuxDebConfig Deb { get; }

    /// <summary><c>[linux.rpm]</c>; empty when absent.</summary>
    public LinuxRpmConfig Rpm { get; }

    /// <summary><c>[linux.apk]</c>; empty when absent.</summary>
    public LinuxApkConfig Apk { get; }

    /// <summary>The package maintainer: <see cref="Maintainer"/>, or <paramref name="app"/>'s publisher.</summary>
    public string MaintainerOf(AppConfig app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return Maintainer ?? app.Publisher;
    }

    /// <summary>The package name: <see cref="Package"/>, or the last segment of <paramref name="app"/>'s id, lower-cased.</summary>
    public string PackageName(AppConfig app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return Package ?? LinuxPackageName.Derive(app.Id);
    }
}

/// <summary><c>[linux.deb]</c>: settings of the deb package only.</summary>
public sealed class LinuxDebConfig
{
    internal LinuxDebConfig(IReadOnlyList<string> depends)
    {
        Depends = depends;
    }

    /// <summary><c>depends</c>: relations for the control file's <c>Depends</c>, written verbatim, in order.</summary>
    public IReadOnlyList<string> Depends { get; }
}

/// <summary><c>[linux.rpm]</c>: settings of the rpm package only.</summary>
public sealed class LinuxRpmConfig
{
    internal LinuxRpmConfig(IReadOnlyList<string> requires, string? license, RpmSigningConfig? signing)
    {
        Requires = requires;
        License = license;
        Signing = signing;
    }

    /// <summary>
    /// <c>requires</c>: dependencies for the header's <c>Requires</c>, each written as in a spec
    /// file's <c>Requires:</c> line with one dependency (<c>name</c>, <c>name &gt;= version</c> or a
    /// rich dependency in parentheses), in order.
    /// </summary>
    public IReadOnlyList<string> Requires { get; }

    /// <summary><c>license</c>: the header's <c>License</c> (an SPDX expression by convention), or null to leave it out.</summary>
    public string? License { get; }

    /// <summary><c>[linux.rpm.signing]</c>, or null to build unsigned packages.</summary>
    public RpmSigningConfig? Signing { get; }
}

/// <summary><c>[linux.rpm.signing]</c>: the OpenPGP key rpm packages are signed with.</summary>
public sealed class RpmSigningConfig
{
    private readonly string _baseDirectory;

    internal RpmSigningConfig(string baseDirectory, ConfigValue key, ConfigValue? password)
    {
        _baseDirectory = baseDirectory;
        Key = key;
        Password = password;
    }

    /// <summary>
    /// <c>key</c>: the ASCII-armored OpenPGP secret key itself (usually <c>{ env = "…" }</c>), or
    /// the path of a file holding it (armored or binary), relative to the configuration directory.
    /// </summary>
    public ConfigValue Key { get; }

    /// <summary><c>password</c>: the key's passphrase, or null for an unprotected key.</summary>
    public ConfigValue? Password { get; }

    /// <summary>
    /// Returns the key: the value itself when it holds an armored block
    /// (<c>-----BEGIN PGP …</c>), else the contents of the file it names.
    /// </summary>
    /// <exception cref="ConfigurationException">A referenced environment variable is not set, or the file cannot be read.</exception>
    public byte[] ResolveKey(Func<string, string?>? environment = null)
    {
        var value = Key.Resolve(environment);
        if (value.Contains("-----BEGIN PGP", StringComparison.Ordinal))
        {
            return System.Text.Encoding.UTF8.GetBytes(value);
        }

        var path = ConfigReader.ResolvePath(_baseDirectory, value.Trim());
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ConfigurationException([new ConfigError(
                $"{Key.Key}: the value is neither an armored OpenPGP key ('-----BEGIN PGP PRIVATE KEY BLOCK-----') nor a readable key file ({path}: {ex.Message})",
                Key.Source,
                Key.Position)]);
        }
    }

    /// <summary>Resolves <see cref="Password"/>, or returns null when none is configured.</summary>
    /// <exception cref="ConfigurationException">A referenced environment variable is not set.</exception>
    public string? ResolvePassword(Func<string, string?>? environment = null) => Password?.Resolve(environment);
}

/// <summary><c>[linux.apk]</c>: settings of the apk package only.</summary>
public sealed class LinuxApkConfig
{
    internal LinuxApkConfig(IReadOnlyList<string> depends, string? license, ApkSigningConfig? signing)
    {
        Depends = depends;
        License = license;
        Signing = signing;
    }

    /// <summary>
    /// <c>depends</c>: dependencies for <c>.PKGINFO</c>, one apk dependency each (<c>name</c>,
    /// <c>so:libname.so.N</c>, <c>name&gt;=version</c>, …), written verbatim, in order.
    /// </summary>
    public IReadOnlyList<string> Depends { get; }

    /// <summary><c>license</c>: <c>.PKGINFO</c>'s <c>license</c> (an SPDX expression by convention), or null to leave it out.</summary>
    public string? License { get; }

    /// <summary><c>[linux.apk.signing]</c>, or null to build unsigned packages.</summary>
    public ApkSigningConfig? Signing { get; }

    /// <summary>
    /// Returns why <paramref name="keyName"/> cannot name a public key in <c>/etc/apk/keys</c>, or
    /// null when it can: one file name of printable ASCII characters without spaces or '/', at
    /// most 80 characters (the signature entry <c>.SIGN.RSA256.&lt;key_name&gt;</c> then fits a tar
    /// header's 100-byte name).
    /// </summary>
    public static string? ValidateKeyName(string keyName)
    {
        ArgumentNullException.ThrowIfNull(keyName);
        if (keyName.Length == 0 || keyName is "." or "..")
        {
            return "is not a file name; use the name of the public key file users install into /etc/apk/keys, such as 'dev@example.com-6123abcd.rsa.pub'";
        }

        if (keyName.Any(c => c is <= ' ' or >= '\u007f' or '/'))
        {
            return "must be a file name of printable ASCII characters without spaces or '/', such as 'dev@example.com-6123abcd.rsa.pub'";
        }

        return keyName.Length > 80 ? "is longer than 80 characters" : null;
    }
}

/// <summary><c>[linux.apk.signing]</c>: the RSA key apk packages are signed with, as <c>abuild-sign</c> uses it.</summary>
public sealed class ApkSigningConfig
{
    private readonly string _baseDirectory;

    internal ApkSigningConfig(string baseDirectory, ConfigValue key, string keyName)
    {
        _baseDirectory = baseDirectory;
        Key = key;
        KeyName = keyName;
    }

    /// <summary>
    /// <c>key</c>: the PEM-encoded RSA private key itself (usually <c>{ env = "…" }</c>), or the
    /// path of a PEM file, relative to the configuration directory.
    /// </summary>
    public ConfigValue Key { get; }

    /// <summary>
    /// <c>key_name</c>: the file name of the public key in <c>/etc/apk/keys</c>, which apk looks
    /// the signature's key up by (abuild-keygen writes <c>&lt;email&gt;-&lt;hex&gt;.rsa.pub</c>).
    /// </summary>
    public string KeyName { get; }

    /// <summary>Returns the PEM text: the value itself when it holds a PEM block (<c>-----BEGIN</c>), else the contents of the file it names.</summary>
    /// <exception cref="ConfigurationException">A referenced environment variable is not set, or the file cannot be read.</exception>
    public string ResolveKey(Func<string, string?>? environment = null)
    {
        var value = Key.Resolve(environment);
        if (value.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            return value;
        }

        var path = ConfigReader.ResolvePath(_baseDirectory, value.Trim());
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new ConfigurationException([new ConfigError(
                $"{Key.Key}: the value is neither a PEM private key ('-----BEGIN PRIVATE KEY-----') nor a readable key file ({path}: {ex.Message})",
                Key.Source,
                Key.Position)]);
        }
    }
}

/// <summary><c>[macos]</c>: macOS package settings.</summary>
public sealed class MacOSConfig : PlatformConfig
{
    internal MacOSConfig(
        bool isConfigured,
        IReadOnlyList<PackageFormat> formats,
        string? bundleId = null,
        ConfigPath? icon = null,
        string? minimumVersion = null,
        string? category = null,
        MacOSSigningConfig? signing = null,
        MacOSNotarizationConfig? notarization = null)
        : base(OperatingSystemFamily.MacOS, isConfigured, formats)
    {
        BundleId = bundleId;
        Icon = icon;
        MinimumVersion = minimumVersion;
        Category = category;
        Signing = signing;
        Notarization = notarization;
    }

    /// <summary><c>bundle_id</c>: the bundle's <c>CFBundleIdentifier</c>, already validated, or null for <c>app.id</c>.</summary>
    public string? BundleId { get; }

    /// <summary><c>icon</c>: an <c>.icns</c> or PNG file, or null to convert <c>[app] icon</c>.</summary>
    public ConfigPath? Icon { get; }

    /// <summary><c>minimum_version</c>: <c>LSMinimumSystemVersion</c> (such as <c>11.0</c>), or null to leave it out.</summary>
    public string? MinimumVersion { get; }

    /// <summary><c>category</c>: <c>LSApplicationCategoryType</c> (such as <c>public.app-category.productivity</c>), or null.</summary>
    public string? Category { get; }

    /// <summary><c>[macos.signing]</c>, or null to sign ad hoc.</summary>
    public MacOSSigningConfig? Signing { get; }

    /// <summary><c>[macos.notarization]</c>, or null to skip notarization.</summary>
    public MacOSNotarizationConfig? Notarization { get; }

    /// <summary>The bundle identifier: <see cref="BundleId"/>, or <paramref name="app"/>'s id.</summary>
    public string BundleIdentifier(AppConfig app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return BundleId ?? app.Id;
    }

    /// <summary>
    /// Returns why <paramref name="value"/> cannot be a <c>CFBundleIdentifier</c>, or null when it
    /// can: ASCII letters, digits, '-' and '.', in at least two non-empty dot-separated segments.
    /// </summary>
    public static string? ValidateBundleId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var segments = value.Split('.');
        if (value.Length is 0 or > 255 || segments.Length < 2 || segments.Any(s => s.Length == 0))
        {
            return "is not a reverse-DNS bundle identifier such as 'com.example.notes'";
        }

        return value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')
            ? null
            : "may contain only ASCII letters, digits, '-' and '.'";
    }
}

/// <summary><c>[macos.signing]</c>: the code-signing identity <c>codesign</c> uses.</summary>
public sealed class MacOSSigningConfig
{
    private readonly string _baseDirectory;

    internal MacOSSigningConfig(string baseDirectory, ConfigValue identity, ConfigValue? keychain, ConfigPath? entitlements, ConfigValue? installerIdentity)
    {
        _baseDirectory = baseDirectory;
        Identity = identity;
        Keychain = keychain;
        Entitlements = entitlements;
        InstallerIdentity = installerIdentity;
    }

    /// <summary>
    /// <c>identity</c>: what <c>codesign --sign</c> takes, the certificate's common name (such as
    /// <c>Developer ID Application: Example Ltd. (TEAMID1234)</c>) or its SHA-1 hash.
    /// </summary>
    public ConfigValue Identity { get; }

    /// <summary><c>keychain</c>: the keychain file holding the identity, or null for the user's keychain search list.</summary>
    public ConfigValue? Keychain { get; }

    /// <summary><c>entitlements</c>: an entitlements property list for the application, or null for none.</summary>
    public ConfigPath? Entitlements { get; }

    /// <summary><c>installer_identity</c>: the installer-signing identity of pkg packages, or null.</summary>
    public ConfigValue? InstallerIdentity { get; }

    /// <summary>
    /// Resolves <see cref="Keychain"/>: an absolute path as it is, a relative path that exists
    /// below the configuration directory as that file, anything else as written (a keychain name
    /// that the Security framework looks up in <c>~/Library/Keychains</c>); null when none is configured.
    /// </summary>
    /// <exception cref="ConfigurationException">A referenced environment variable is not set.</exception>
    public string? ResolveKeychain(Func<string, string?>? environment = null)
    {
        if (Keychain is null)
        {
            return null;
        }

        var value = Keychain.Resolve(environment);
        if (Path.IsPathRooted(value))
        {
            return value;
        }

        var path = ConfigReader.ResolvePath(_baseDirectory, value);
        return File.Exists(path) ? path : value;
    }
}

/// <summary><c>[macos.notarization]</c>: the App Store Connect API key <c>notarytool</c> uses.</summary>
public sealed class MacOSNotarizationConfig
{
    private readonly string _baseDirectory;

    internal MacOSNotarizationConfig(string baseDirectory, ConfigValue key, ConfigValue keyId, ConfigValue issuer)
    {
        _baseDirectory = baseDirectory;
        Key = key;
        KeyId = keyId;
        Issuer = issuer;
    }

    /// <summary><c>key</c>: the path of the API key file (<c>AuthKey_&lt;id&gt;.p8</c>), relative to the configuration directory.</summary>
    public ConfigValue Key { get; }

    /// <summary><c>key_id</c>: the API key's ID.</summary>
    public ConfigValue KeyId { get; }

    /// <summary><c>issuer</c>: the issuer ID of the App Store Connect team.</summary>
    public ConfigValue Issuer { get; }

    /// <summary>Resolves <see cref="Key"/> to an absolute path.</summary>
    /// <exception cref="ConfigurationException">A referenced environment variable is not set.</exception>
    public string ResolveKeyPath(Func<string, string?>? environment = null) =>
        ConfigReader.ResolvePath(_baseDirectory, Key.Resolve(environment));
}

/// <summary><c>[windows.signing]</c>: the Authenticode certificate.</summary>
public sealed class WindowsSigningConfig
{
    private readonly string _baseDirectory;

    internal WindowsSigningConfig(string baseDirectory, ConfigValue certificate, ConfigValue? password, ConfigValue? timestamp)
    {
        _baseDirectory = baseDirectory;
        Certificate = certificate;
        Password = password;
        Timestamp = timestamp;
    }

    /// <summary><c>certificate</c>: the PFX file path (relative paths are resolved against the configuration directory).</summary>
    public ConfigValue Certificate { get; }

    /// <summary><c>password</c>, or null for a PFX without a password.</summary>
    public ConfigValue? Password { get; }

    /// <summary><c>timestamp</c>: the RFC 3161 timestamp authority URL, or null to sign without a timestamp.</summary>
    public ConfigValue? Timestamp { get; }

    /// <summary>Resolves <see cref="Certificate"/> to an absolute path.</summary>
    /// <exception cref="ConfigurationException">A referenced environment variable is not set.</exception>
    public string ResolveCertificatePath(Func<string, string?>? environment = null) =>
        ConfigReader.ResolvePath(_baseDirectory, Certificate.Resolve(environment));

    /// <summary>Resolves <see cref="Password"/>, or returns null when none is configured.</summary>
    /// <exception cref="ConfigurationException">A referenced environment variable is not set.</exception>
    public string? ResolvePassword(Func<string, string?>? environment = null) => Password?.Resolve(environment);

    /// <summary>Resolves <see cref="Timestamp"/> to an absolute http or https URL, or returns null when none is configured.</summary>
    /// <exception cref="ConfigurationException">A referenced environment variable is not set, or its value is not an http or https URL.</exception>
    public Uri? ResolveTimestamp(Func<string, string?>? environment = null)
    {
        if (Timestamp is null)
        {
            return null;
        }

        var text = Timestamp.Resolve(environment);
        return ConfigReader.TryParseHttpUrl(text, out var url)
            ? url
            : throw new ConfigurationException([new ConfigError(
                $"{Timestamp.Key}: '{text}' is not an absolute http or https URL", Timestamp.Source, Timestamp.Position)]);
    }
}

/// <summary><c>[windows.uninstall]</c>: extra cleanup on uninstall.</summary>
public sealed class WindowsUninstallConfig
{
    internal WindowsUninstallConfig(IReadOnlyList<string> remove) => Remove = remove;

    /// <summary>
    /// <c>remove</c>: extra paths the uninstaller deletes (for example the user data directory),
    /// as written. Their syntax is interpreted by the Windows installer back end.
    /// </summary>
    public IReadOnlyList<string> Remove { get; }
}

/// <summary>A trusted update key from <c>[updates] keys</c>: a literal base64 key or <c>{ file = "…" }</c>.</summary>
public sealed class UpdateKeyConfig
{
    internal UpdateKeyConfig(string key, string? literal, ConfigPath? file, TomlPosition position)
    {
        Key = key;
        Literal = literal;
        File = file;
        Position = position;
    }

    /// <summary>The element's name in messages, e.g. <c>updates.keys[1]</c>.</summary>
    public string Key { get; }

    /// <summary>The canonical base64 key, or null for a file reference.</summary>
    public string? Literal { get; }

    /// <summary>The <c>.pub</c> file, or null for a literal key.</summary>
    public ConfigPath? File { get; }

    public TomlPosition Position { get; }
}

/// <summary>
/// <c>[updates]</c>: where installs look for updates and which keys they trust
/// (docs/design/0005-nsis-updates.md section 1).
/// </summary>
public sealed class UpdatesConfig
{
    /// <summary>The placeholder <c>feed</c> must contain.</summary>
    public const string ChannelPlaceholder = "{channel}";

    public const string FilePlaceholder = "{file}";

    public const string VersionPlaceholder = "{version}";

    private readonly string _source;

    internal UpdatesConfig(string source, string feed, string download, string channel, IReadOnlyList<UpdateKeyConfig> keys)
    {
        _source = source;
        FeedTemplate = feed;
        DownloadTemplate = download;
        Channel = channel;
        Keys = keys;
    }

    /// <summary><c>feed</c>: the feed URL template, containing <c>{channel}</c>.</summary>
    public string FeedTemplate { get; }

    /// <summary><c>download</c>: the artifact URL template, with <c>{file}</c>, <c>{version}</c> and <c>{channel}</c>.</summary>
    public string DownloadTemplate { get; }

    /// <summary>The channel: <c>--channel</c>, else <c>channel</c>, else <c>stable</c>.</summary>
    public string Channel { get; }

    /// <summary><c>keys</c>: at least one trusted key.</summary>
    public IReadOnlyList<UpdateKeyConfig> Keys { get; }

    /// <summary>The feed URL of <see cref="Channel"/>.</summary>
    public Uri FeedUrl => Expand(FeedTemplate, Channel, null, null);

    /// <summary>The download URL of an artifact of <see cref="Channel"/>.</summary>
    public Uri DownloadUrl(string file, string version) => Expand(DownloadTemplate, Channel, file, version);

    /// <summary>Returns every key as canonical base64 text, reading key files.</summary>
    /// <exception cref="ConfigurationException">A key file cannot be read or holds no valid key.</exception>
    public IReadOnlyList<string> ResolveKeys()
    {
        var keys = new List<string>();
        var errors = new List<ConfigError>();
        foreach (var key in Keys)
        {
            if (key.Literal is { } literal)
            {
                keys.Add(literal);
                continue;
            }

            var file = key.File!;
            string text;
            try
            {
                text = System.IO.File.ReadAllText(file.FullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(new ConfigError($"{key.Key}: cannot read the key file {file.FullPath}: {ex.Message}", _source, key.Position));
                continue;
            }

            if (UpdateRules.TryDecodePublicKey(text, out var bytes, out var error))
            {
                keys.Add(Convert.ToBase64String(bytes));
            }
            else
            {
                errors.Add(new ConfigError($"{key.Key}: {file.FullPath} holds no valid update public key: {error}", _source, key.Position));
            }
        }

        if (errors.Count > 0)
        {
            throw new ConfigurationException(errors);
        }

        return keys.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Replaces the placeholders with URL-escaped values; the template was validated when it was read.</summary>
    internal static Uri Expand(string template, string channel, string? file, string? version)
    {
        var text = template.Replace(ChannelPlaceholder, Uri.EscapeDataString(channel), StringComparison.Ordinal);
        if (file is not null)
        {
            text = text.Replace(FilePlaceholder, Uri.EscapeDataString(file), StringComparison.Ordinal);
        }

        if (version is not null)
        {
            text = text.Replace(VersionPlaceholder, Uri.EscapeDataString(version), StringComparison.Ordinal);
        }

        return new Uri(text, UriKind.Absolute);
    }
}
