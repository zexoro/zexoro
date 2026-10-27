namespace Zexoro.Toolsets;

/// <summary>A pinned toolset archive (<c>.tar.gz</c>) and where to read it from.</summary>
/// <param name="Name">The toolset name, e.g. <c>nsis</c>.</param>
/// <param name="Version">The pinned version, e.g. <c>3.13-zexoro.1</c>.</param>
/// <param name="Sha256">The expected SHA-256 of the archive, as 64 lower-case hex digits.</param>
/// <param name="Open">Opens the archive for reading (for example an embedded resource).</param>
public sealed record ToolsetArchive(string Name, string Version, string Sha256, Func<Stream> Open)
{
    /// <summary>The cache directory name: name, version and the start of the hash, so a re-pinned archive never reuses an old directory.</summary>
    public string DirectoryName => $"{Name}-{Version}-{Sha256[..16]}";

    internal void Validate()
    {
        if (!IsSafeName(Name) || !IsSafeName(Version))
        {
            throw new ArgumentException($"Toolset name '{Name}' and version '{Version}' may only contain letters, digits, '.', '-' and '_'.");
        }

        if (Sha256.Length != 64 || !Sha256.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
        {
            throw new ArgumentException($"Toolset hash '{Sha256}' is not 64 lower-case hexadecimal digits.");
        }
    }

    private static bool IsSafeName(string text) =>
        text.Length > 0 && text[0] != '.' && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
