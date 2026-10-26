using Zexoro.Toml;

namespace Zexoro.Configuration;

/// <summary>One problem with a configuration.</summary>
/// <param name="Message">What is wrong, naming the key, e.g. <c>app.version: ...</c>.</param>
/// <param name="Source">Where the value came from: the configuration file path, or a command-line option such as <c>--version</c>.</param>
/// <param name="Position">The line and column in <paramref name="Source"/>, or null when the problem has no location (a missing table, a command-line value).</param>
public sealed record ConfigError(string Message, string Source, TomlPosition? Position)
{
    /// <summary>Returns <c>source:line:column: message</c>, or <c>source: message</c> without a position.</summary>
    public override string ToString() =>
        Position is { } p ? $"{Source}:{p.Line}:{p.Column}: {Message}" : $"{Source}: {Message}";
}
