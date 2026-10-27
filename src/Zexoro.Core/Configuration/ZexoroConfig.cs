using Zexoro.Toml;
using Zexoro.Versioning;

namespace Zexoro.Configuration;

/// <summary>
/// A validated <c>zexoro.toml</c>. Each top-level table maps to one property; platform tables
/// (<c>[windows]</c> today) are optional, and new platforms, formats or update settings are added
/// as further tables without changing the existing ones.
/// </summary>
public sealed class ZexoroConfig
{
    /// <summary>The configuration file name that <c>zexoro init</c> writes and the CLI looks for.</summary>
    public const string DefaultFileName = "zexoro.toml";

    internal ZexoroConfig(string source, string baseDirectory, AppConfig app, InputConfig input, WindowsConfig? windows, UpdatesConfig? updates)
    {
        Updates = updates;
        Source = source;
        BaseDirectory = baseDirectory;
        App = app;
        Input = input;
        Windows = windows;
    }

    /// <summary>The configuration file path as given, used in messages.</summary>
    public string Source { get; }

    /// <summary>The absolute directory that relative paths are resolved against: the configuration file's directory.</summary>
    public string BaseDirectory { get; }

    /// <summary><c>[app]</c>: what the application is.</summary>
    public AppConfig App { get; }

    /// <summary><c>[input]</c>: the build output to package.</summary>
    public InputConfig Input { get; }

    /// <summary><c>[windows]</c>, or null when the table is absent (no Windows target configured).</summary>
    public WindowsConfig? Windows { get; }

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

/// <summary><c>[input]</c>: the already-built application directory.</summary>
public sealed class InputConfig
{
    internal InputConfig(ConfigPath directory, ConfigPath main)
    {
        Directory = directory;
        Main = main;
    }

    /// <summary>The build output directory.</summary>
    public ConfigPath Directory { get; }

    /// <summary>
    /// The entry point. <see cref="ConfigPath.Value"/> is relative to <see cref="Directory"/> (as
    /// written, with forward slashes); <see cref="ConfigPath.FullPath"/> is absolute.
    /// </summary>
    public ConfigPath Main { get; }
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
public sealed class WindowsConfig
{
    internal WindowsConfig(
        WindowsInstallScope installScope,
        WindowsShortcuts shortcuts,
        ConfigPath? license,
        WindowsSignFiles sign,
        WindowsSigningConfig? signing,
        WindowsUninstallConfig uninstall)
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
