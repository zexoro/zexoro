namespace Zexoro.Toml;

/// <summary>
/// A position in a TOML source text. Lines and columns start at 1; columns count Unicode
/// scalar values (characters), not bytes or UTF-16 code units.
/// </summary>
public readonly record struct TomlPosition(int Line, int Column)
{
    /// <summary>Returns <c>line L, column C</c>.</summary>
    public override string ToString() => $"line {Line}, column {Column}";
}
