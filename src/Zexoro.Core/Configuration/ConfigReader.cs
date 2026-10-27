using System.Diagnostics.CodeAnalysis;
using Zexoro.Toml;
using Zexoro.Versioning;

namespace Zexoro.Configuration;

/// <summary>
/// Maps a parsed TOML document onto <see cref="ZexoroConfig"/> by hand (no reflection), collecting
/// every error instead of stopping at the first.
/// </summary>
internal sealed class ConfigReader
{
    /// <summary>Folders that <c>[windows.uninstall] remove</c> entries must start with.</summary>
    internal static readonly string[] UninstallRoots = ["%LOCALAPPDATA%", "%APPDATA%", "%PROGRAMDATA%"];

    private static readonly string[] RootKeys = ["app", "input", "windows", "updates"];
    private static readonly string[] AppKeys = ["id", "name", "version", "publisher", "icon", "description", "url"];
    private static readonly string[] InputKeys = ["dir", "main"];
    private static readonly string[] WindowsKeys = ["install_scope", "shortcuts", "license", "sign", "signing", "uninstall"];
    private static readonly string[] SigningKeys = ["certificate", "password", "timestamp"];
    private static readonly string[] UninstallKeys = ["remove"];
    private static readonly string[] UpdatesKeys = ["feed", "download", "channel", "keys"];

    private readonly List<ConfigError> _errors = [];
    private readonly string _source;
    private readonly string _baseDirectory;

    private ConfigReader(string source, string baseDirectory)
    {
        _source = source;
        _baseDirectory = baseDirectory;
    }

    public static ZexoroConfig Read(TomlTable root, string source, string baseDirectory, ConfigOverrides? overrides)
    {
        var reader = new ConfigReader(source, baseDirectory);
        var config = reader.ReadRoot(root, overrides);
        if (reader._errors.Count > 0)
        {
            throw new ConfigurationException(reader._errors
                .OrderBy(e => e.Position?.Line ?? int.MaxValue)
                .ThenBy(e => e.Position?.Column ?? int.MaxValue));
        }

        return config!;
    }

    internal static string ResolvePath(string baseDirectory, string path) =>
        Path.GetFullPath(Path.Combine(baseDirectory, path.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar)));

    internal static bool TryParseHttpUrl(string text, [NotNullWhen(true)] out Uri? url) =>
        Uri.TryCreate(text, UriKind.Absolute, out url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Whether <paramref name="id"/> is a reverse-DNS identifier: at least two dot-separated
    /// segments of ASCII letters, digits and hyphens, each starting with a letter and not ending
    /// with a hyphen.
    /// </summary>
    internal static bool IsReverseDns(string id)
    {
        if (id.Length > 255)
        {
            return false;
        }

        var segments = id.Split('.');
        return segments.Length >= 2 && segments.All(s =>
            s.Length > 0
            && char.IsAsciiLetter(s[0])
            && s[^1] != '-'
            && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }

    private ZexoroConfig? ReadRoot(TomlTable root, ConfigOverrides? overrides)
    {
        var tables = new TableReader(this, root, null, RootKeys);
        AppConfig? app = null;
        if (tables.RequiredTable("app", AppKeys) is { } appTable)
        {
            app = ReadApp(appTable, overrides);
        }
        else if (overrides?.Version is { } version)
        {
            ParseOverrideVersion(version);
        }

        var input = tables.RequiredTable("input", InputKeys) is { } inputTable ? ReadInput(inputTable) : null;
        var windows = tables.OptionalTable("windows", WindowsKeys) is { } windowsTable ? ReadWindows(windowsTable) : null;
        UpdatesConfig? updates = null;
        if (tables.OptionalTable("updates", UpdatesKeys) is { } updatesTable)
        {
            updates = ReadUpdates(updatesTable, overrides);
        }
        else if (overrides?.Channel is not null)
        {
            _errors.Add(new ConfigError("there is no [updates] table, so there is no channel to set", "--channel", null));
        }

        tables.ReportUnknownKeys();

        return app is null || input is null || _errors.Count > 0
            ? null
            : new ZexoroConfig(_source, _baseDirectory, app, input, windows, updates);
    }

    private AppConfig? ReadApp(TableReader table, ConfigOverrides? overrides)
    {
        var id = table.RequiredString("id");
        if (id is not null && !IsReverseDns(id.Value))
        {
            Error(id, $"app.id: '{id.Value}' is not a reverse-DNS identifier such as 'com.example.notes' "
                + "(two or more dot-separated segments of letters, digits and hyphens, each starting with a letter)");
        }

        var name = table.RequiredString("name");
        if (name is not null && ValidateFileName(name.Value) is { } nameProblem)
        {
            Error(name, $"app.name: {nameProblem}");
        }

        SemanticVersion? version;
        if (overrides?.Version is { } overrideVersion)
        {
            table.OptionalString("version");
            version = ParseOverrideVersion(overrideVersion);
        }
        else
        {
            version = null;
            if (table.RequiredString("version") is { } versionNode)
            {
                if (SemanticVersion.TryParse(versionNode.Value, out var parsed, out var problem))
                {
                    version = parsed;
                }
                else
                {
                    Error(versionNode, $"app.version: '{versionNode.Value}' is not a Semantic Versioning 2.0.0 version: {problem}");
                }
            }
        }

        var publisher = table.RequiredString("publisher");
        if (publisher is not null && string.IsNullOrWhiteSpace(publisher.Value))
        {
            Error(publisher, "app.publisher must not be empty");
        }

        var icon = table.RequiredPath("icon");
        var description = table.OptionalString("description");
        Uri? url = null;
        if (table.OptionalString("url") is { } urlNode && !TryParseHttpUrl(urlNode.Value, out url))
        {
            Error(urlNode, $"app.url: '{urlNode.Value}' is not an absolute http or https URL");
        }

        table.ReportUnknownKeys();
        return id is null || name is null || version is null || publisher is null || icon is null
            ? null
            : new AppConfig(id.Value, name.Value, version, publisher.Value, icon, description?.Value, url);
    }

    private SemanticVersion? ParseOverrideVersion(string text)
    {
        if (SemanticVersion.TryParse(text, out var version, out var problem))
        {
            return version;
        }

        _errors.Add(new ConfigError($"'{text}' is not a Semantic Versioning 2.0.0 version: {problem}", "--version", null));
        return null;
    }

    private InputConfig? ReadInput(TableReader table)
    {
        var dir = table.RequiredPath("dir");
        ConfigPath? main = null;
        if (table.RequiredString("main") is { } mainNode)
        {
            var normalized = mainNode.Value.Replace('\\', '/');
            if (normalized.Length == 0 || normalized.EndsWith('/'))
            {
                Error(mainNode, "input.main must name a file inside input.dir");
            }
            else if (normalized.StartsWith('/') || Path.IsPathRooted(normalized) || (normalized.Length >= 2 && normalized[1] == ':'))
            {
                Error(mainNode, "input.main must be a path relative to input.dir, not an absolute path");
            }
            else if (normalized.Split('/').Any(segment => segment is ".." or "."))
            {
                Error(mainNode, "input.main must not contain '.' or '..' segments");
            }
            else if (dir is not null)
            {
                main = new ConfigPath("input.main", normalized, ResolvePath(dir.FullPath, normalized), mainNode.Position);
            }
        }

        table.ReportUnknownKeys();
        return dir is null || main is null ? null : new InputConfig(dir, main);
    }

    private WindowsConfig? ReadWindows(TableReader table)
    {
        var scope = table.OptionalEnum("install_scope", ["user", "machine"]) switch
        {
            "machine" => WindowsInstallScope.Machine,
            _ => WindowsInstallScope.User,
        };

        var shortcuts = WindowsShortcuts.StartMenu;
        if (table.OptionalArray("shortcuts") is { } shortcutArray)
        {
            shortcuts = WindowsShortcuts.None;
            for (var i = 0; i < shortcutArray.Count; i++)
            {
                var flag = table.EnumElement(shortcutArray, i, "windows.shortcuts", ["start-menu", "desktop"]) switch
                {
                    "start-menu" => WindowsShortcuts.StartMenu,
                    "desktop" => WindowsShortcuts.Desktop,
                    _ => WindowsShortcuts.None,
                };
                if (flag != WindowsShortcuts.None && shortcuts.HasFlag(flag))
                {
                    Error(shortcutArray[i], $"windows.shortcuts: '{((TomlString)shortcutArray[i]).Value}' is listed twice");
                }

                shortcuts |= flag;
            }
        }

        var license = table.OptionalPath("license");
        var sign = table.OptionalEnum("sign", ["main", "all"]) switch
        {
            "all" => WindowsSignFiles.All,
            _ => WindowsSignFiles.Main,
        };

        WindowsSigningConfig? signing = null;
        if (table.OptionalTable("signing", SigningKeys) is { } signingTable)
        {
            var certificate = signingTable.RequiredValue("certificate");
            var password = signingTable.OptionalValue("password");
            var timestamp = signingTable.OptionalValue("timestamp");
            if (timestamp is { Literal: { } literal } && !TryParseHttpUrl(literal, out _))
            {
                Error(timestamp.Position, $"windows.signing.timestamp: '{literal}' is not an absolute http or https URL");
            }

            signingTable.ReportUnknownKeys();
            if (certificate is not null)
            {
                signing = new WindowsSigningConfig(_baseDirectory, certificate, password, timestamp);
            }
        }

        var remove = new List<string>();
        if (table.OptionalTable("uninstall", UninstallKeys) is { } uninstallTable)
        {
            if (uninstallTable.OptionalArray("remove") is { } removeArray)
            {
                for (var i = 0; i < removeArray.Count; i++)
                {
                    if (uninstallTable.StringElement(removeArray, i, "windows.uninstall.remove") is not { } path)
                    {
                        continue;
                    }

                    if (ValidateUninstallPath(path.Value) is { } problem)
                    {
                        Error(path, $"windows.uninstall.remove: {problem}");
                    }
                    else
                    {
                        remove.Add(path.Value);
                    }
                }
            }

            uninstallTable.ReportUnknownKeys();
        }

        table.ReportUnknownKeys();
        return new WindowsConfig(scope, shortcuts, license, sign, signing, new WindowsUninstallConfig(remove));
    }

    private UpdatesConfig? ReadUpdates(TableReader table, ConfigOverrides? overrides)
    {
        var feed = table.RequiredString("feed");
        if (feed is not null)
        {
            if (!feed.Value.Contains(UpdatesConfig.ChannelPlaceholder, StringComparison.Ordinal))
            {
                Error(feed, $"updates.feed: '{feed.Value}' must contain {UpdatesConfig.ChannelPlaceholder}, so every channel has its own feed");
                feed = null;
            }
            else if (ValidateUrlTemplate(feed.Value, [UpdatesConfig.ChannelPlaceholder]) is { } problem)
            {
                Error(feed, $"updates.feed: {problem}");
                feed = null;
            }
        }

        var download = table.RequiredString("download");
        if (download is not null
            && ValidateUrlTemplate(download.Value, [UpdatesConfig.FilePlaceholder, UpdatesConfig.VersionPlaceholder, UpdatesConfig.ChannelPlaceholder]) is { } downloadProblem)
        {
            Error(download, $"updates.download: {downloadProblem}");
            download = null;
        }

        string? channel = UpdateRules.DefaultChannel;
        const string ChannelRule = "is not a valid channel (ASCII letters, digits, '.', '-' and '_', not starting with '.')";
        if (table.OptionalString("channel") is { } channelNode)
        {
            channel = channelNode.Value;
            if (!UpdateRules.IsValidChannel(channelNode.Value))
            {
                Error(channelNode, $"updates.channel: '{channelNode.Value}' {ChannelRule}");
                channel = null;
            }
        }

        if (overrides?.Channel is { } overrideChannel)
        {
            channel = overrideChannel;
            if (!UpdateRules.IsValidChannel(overrideChannel))
            {
                _errors.Add(new ConfigError($"'{overrideChannel}' {ChannelRule}", "--channel", null));
                channel = null;
            }
        }

        var keys = new List<UpdateKeyConfig>();
        var keysValid = true;
        if (!table.Contains("keys"))
        {
            table.Missing("keys");
            keysValid = false;
        }
        else if (table.OptionalArray("keys") is { } keyArray)
        {
            if (keyArray.Count == 0)
            {
                Error(keyArray, "updates.keys must list at least one public key (see 'zexoro keys new')");
                keysValid = false;
            }

            for (var i = 0; i < keyArray.Count; i++)
            {
                var name = $"updates.keys[{i}]";
                switch (keyArray[i])
                {
                    case TomlString literal:
                        if (UpdateRules.TryDecodePublicKey(literal.Value, out var bytes, out var keyError))
                        {
                            keys.Add(new UpdateKeyConfig(name, Convert.ToBase64String(bytes), null, literal.Position));
                        }
                        else
                        {
                            Error(literal, $"{name}: not a valid update public key (base64 of a P-256 SubjectPublicKeyInfo, as 'zexoro keys new' writes): {keyError}");
                            keysValid = false;
                        }

                        break;
                    case TomlTable reference:
                        var fileTable = new TableReader(this, reference, name, ["file"]);
                        var path = fileTable.RequiredPath("file");
                        fileTable.ReportUnknownKeys();
                        if (path is null)
                        {
                            keysValid = false;
                        }
                        else
                        {
                            keys.Add(new UpdateKeyConfig(name, null, path, reference.Position));
                        }

                        break;
                    default:
                        Error(keyArray[i], $"{name} must be a base64 public key or {{ file = \"<key>.pub\" }}, but is {Describe(keyArray[i])}");
                        keysValid = false;
                        break;
                }
            }
        }
        else
        {
            keysValid = false;
        }

        table.ReportUnknownKeys();
        return feed is null || download is null || channel is null || !keysValid
            ? null
            : new UpdatesConfig(_source, feed.Value, download.Value, channel, keys);
    }

    /// <summary>
    /// Checks a URL template: only <paramref name="placeholders"/> may appear, and with sample values
    /// it must be an https URL (http only for localhost, 127.0.0.1 and [::1]).
    /// </summary>
    private static string? ValidateUrlTemplate(string template, string[] placeholders)
    {
        var rest = template;
        foreach (var placeholder in placeholders)
        {
            rest = rest.Replace(placeholder, string.Empty, StringComparison.Ordinal);
        }

        if (rest.Contains('{', StringComparison.Ordinal) || rest.Contains('}', StringComparison.Ordinal))
        {
            return $"'{template}' contains an unknown placeholder; it may use {Quote(placeholders)}";
        }

        var sample = template
            .Replace(UpdatesConfig.ChannelPlaceholder, "stable", StringComparison.Ordinal)
            .Replace(UpdatesConfig.FilePlaceholder, "App-1.0.0-win-x64.exe", StringComparison.Ordinal)
            .Replace(UpdatesConfig.VersionPlaceholder, "1.0.0", StringComparison.Ordinal);
        return Uri.TryCreate(sample, UriKind.Absolute, out var url) && UpdateRules.IsAllowedUrl(url) && string.IsNullOrEmpty(url.UserInfo)
            ? null
            : $"'{template}' is not an https URL (plain http is accepted only for localhost, 127.0.0.1 and [::1])";
    }

    private static string? ValidateFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "must not be empty";
        }

        if (name != name.Trim())
        {
            return "must not start or end with whitespace";
        }

        if (name.EndsWith('.'))
        {
            return "must not end with '.'";
        }

        var bad = name.FirstOrDefault(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c, StringComparison.Ordinal));
        return bad != default
            ? $"'{name}' contains '{(char.IsControl(bad) ? $"U+{(int)bad:X4}" : bad.ToString())}', which is not allowed in a directory name"
            : null;
    }

    private static string? ValidateUninstallPath(string path)
    {
        var normalized = path.Replace('/', '\\');
        var root = UninstallRoots.FirstOrDefault(r => normalized.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
        var expected = $"must start with {string.Join(", ", UninstallRoots)} followed by a sub-path, such as '%LOCALAPPDATA%\\Example Notes'";
        if (root is null)
        {
            return $"'{path}' {expected}";
        }

        var segments = normalized[(root.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return $"'{path}' {expected}";
        }

        return segments.Any(s => s is "." or "..") ? $"'{path}' must not contain '.' or '..' segments" : null;
    }

    private void Error(TomlNode node, string message) => Error(node.Position, message);

    private void Error(TomlPosition position, string message) => _errors.Add(new ConfigError(message, _source, position));

    private void Error(string message) => _errors.Add(new ConfigError(message, _source, null));

    private static string Describe(TomlNode node) => node.Kind switch
    {
        TomlNodeKind.Table => "a table",
        TomlNodeKind.Array => "an array",
        TomlNodeKind.String => "a string",
        TomlNodeKind.Integer => "an integer",
        TomlNodeKind.Float => "a float",
        TomlNodeKind.Boolean => "a boolean",
        TomlNodeKind.OffsetDateTime => "a date-time",
        TomlNodeKind.LocalDateTime => "a local date-time",
        TomlNodeKind.LocalDate => "a local date",
        TomlNodeKind.LocalTime => "a local time",
        _ => "a value",
    };

    private static string Quote(IEnumerable<string> values)
    {
        var quoted = values.Select(v => $"'{v}'").ToList();
        return quoted.Count switch
        {
            1 => quoted[0],
            2 => $"{quoted[0]} or {quoted[1]}",
            _ => $"{string.Join(", ", quoted.Take(quoted.Count - 1))} or {quoted[^1]}",
        };
    }

    private static string Suggest(string value, IEnumerable<string> candidates) =>
        Suggestions.Closest(value, candidates) is { } match ? $"; did you mean '{match}'?" : string.Empty;

    /// <summary>Reads one table: typed accessors plus unknown-key detection.</summary>
    private sealed class TableReader(ConfigReader owner, TomlTable table, string? path, string[] knownKeys)
    {
        private string Name(string key) => path is null ? key : $"{path}.{key}";

        public TableReader? RequiredTable(string key, string[] childKeys)
        {
            if (!table.TryGetValue(key, out var node))
            {
                owner.Error(path is null ? $"missing required table [{key}]" : $"[{path}]: missing required table [{Name(key)}]");
                return null;
            }

            return AsTable(key, node, childKeys);
        }

        public bool Contains(string key) => table.ContainsKey(key);

        public void Missing(string key) => MissingKey(key);

        public TableReader? OptionalTable(string key, string[] childKeys) =>
            table.TryGetValue(key, out var node) ? AsTable(key, node, childKeys) : null;

        private TableReader? AsTable(string key, TomlNode node, string[] childKeys)
        {
            if (node is TomlTable child)
            {
                return new TableReader(owner, child, Name(key), childKeys);
            }

            owner.Error(node, $"{Name(key)} must be a table, but is {Describe(node)}");
            return null;
        }

        public TomlString? RequiredString(string key)
        {
            if (!table.TryGetValue(key, out var node))
            {
                MissingKey(key);
                return null;
            }

            return AsString(Name(key), node);
        }

        public TomlString? OptionalString(string key) =>
            table.TryGetValue(key, out var node) ? AsString(Name(key), node) : null;

        public TomlArray? OptionalArray(string key)
        {
            if (!table.TryGetValue(key, out var node))
            {
                return null;
            }

            if (node is TomlArray array)
            {
                return array;
            }

            owner.Error(node, $"{Name(key)} must be an array, but is {Describe(node)}");
            return null;
        }

        public ConfigPath? RequiredPath(string key) =>
            RequiredString(key) is { } node ? ToPath(key, node) : null;

        public ConfigPath? OptionalPath(string key) =>
            OptionalString(key) is { } node ? ToPath(key, node) : null;

        private ConfigPath? ToPath(string key, TomlString node)
        {
            if (string.IsNullOrWhiteSpace(node.Value))
            {
                owner.Error(node, $"{Name(key)} must not be empty");
                return null;
            }

            if (node.Value.Contains('\0', StringComparison.Ordinal))
            {
                owner.Error(node, $"{Name(key)} must not contain a NUL character");
                return null;
            }

            return new ConfigPath(Name(key), node.Value, ResolvePath(owner._baseDirectory, node.Value), node.Position);
        }

        public string? OptionalEnum(string key, string[] allowed) =>
            OptionalString(key) is { } node ? CheckEnum(Name(key), node, allowed) : null;

        public string? EnumElement(TomlArray array, int index, string name, string[] allowed) =>
            StringElement(array, index, name) is { } node ? CheckEnum(name, node, allowed) : null;

        public TomlString? StringElement(TomlArray array, int index, string name) =>
            AsString($"{name}[{index}]", array[index]);

        private string? CheckEnum(string name, TomlString node, string[] allowed)
        {
            if (allowed.Contains(node.Value, StringComparer.Ordinal))
            {
                return node.Value;
            }

            owner.Error(node, $"{name} must be {Quote(allowed)}, but is '{node.Value}'{Suggest(node.Value, allowed)}");
            return null;
        }

        /// <summary>Reads a string that may be written literally or as <c>{ env = "NAME" }</c>.</summary>
        public ConfigValue? RequiredValue(string key)
        {
            if (!table.ContainsKey(key))
            {
                MissingKey(key);
                return null;
            }

            return OptionalValue(key);
        }

        public ConfigValue? OptionalValue(string key)
        {
            if (!table.TryGetValue(key, out var node))
            {
                return null;
            }

            var name = Name(key);
            switch (node)
            {
                case TomlString s when s.Value.Length == 0:
                    owner.Error(node, $"{name} must not be empty");
                    return null;
                case TomlString s:
                    return new ConfigValue(name, s.Value, null, owner._source, s.Position);
                case TomlTable reference:
                    var env = new TableReader(owner, reference, name, ["env"]);
                    var variable = env.RequiredString("env");
                    env.ReportUnknownKeys();
                    if (variable is null)
                    {
                        return null;
                    }

                    if (!IsEnvironmentVariableName(variable.Value))
                    {
                        owner.Error(variable, $"{name}.env: '{variable.Value}' is not a valid environment variable name (letters, digits and '_', not starting with a digit)");
                        return null;
                    }

                    return new ConfigValue(name, null, variable.Value, owner._source, reference.Position);
                default:
                    owner.Error(node, $"{name} must be a string or {{ env = \"NAME\" }}, but is {Describe(node)}");
                    return null;
            }
        }

        public void ReportUnknownKeys()
        {
            foreach (var (key, node) in table.Entries)
            {
                if (knownKeys.Contains(key.Name, StringComparer.Ordinal))
                {
                    continue;
                }

                var what = node is TomlTable ? $"unknown table [{Name(key.Name)}]" : $"unknown key '{Name(key.Name)}'";
                owner.Error(key.Position, what + Suggest(key.Name, knownKeys));
            }
        }

        private void MissingKey(string key) =>
            owner.Error(table.Position, path is null
                ? $"missing required key '{key}'"
                : $"[{path}]: missing required key '{key}'");

        private TomlString? AsString(string name, TomlNode node)
        {
            if (node is TomlString s)
            {
                return s;
            }

            owner.Error(node, $"{name} must be a string, but is {Describe(node)}");
            return null;
        }

        private static bool IsEnvironmentVariableName(string name) =>
            name.Length > 0
            && !char.IsAsciiDigit(name[0])
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }
}
