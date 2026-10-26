using Zexoro.Toml;

namespace Zexoro.Configuration;

/// <summary>A file system path from the configuration.</summary>
/// <param name="Key">The dotted key, e.g. <c>app.icon</c>.</param>
/// <param name="Value">The path as written.</param>
/// <param name="FullPath">The absolute path, resolved against the configuration file's directory.</param>
/// <param name="Position">Where the value is written.</param>
public sealed record ConfigPath(string Key, string Value, string FullPath, TomlPosition Position);
