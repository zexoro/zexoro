namespace Zexoro.Configuration;

/// <summary>Values given on the command line that replace values in the configuration file.</summary>
public sealed record ConfigOverrides
{
    /// <summary>Replaces <c>app.version</c> (<c>--version</c>). It must be a Semantic Versioning 2.0.0 version.</summary>
    public string? Version { get; init; }

    /// <summary>Replaces <c>updates.channel</c> (<c>--channel</c>). It needs an <c>[updates]</c> table.</summary>
    public string? Channel { get; init; }

    /// <summary>
    /// Replaces <c>input.dir</c> (<c>--input</c>). Relative paths are resolved against the current
    /// directory; callers with another working directory pass an absolute path.
    /// </summary>
    public string? InputDirectory { get; init; }

    /// <summary>Replaces <c>input.main</c> (<c>--main</c>), relative to the input directory.</summary>
    public string? Main { get; init; }
}
