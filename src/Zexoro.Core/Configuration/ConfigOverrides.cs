namespace Zexoro.Configuration;

/// <summary>Values given on the command line that replace values in the configuration file.</summary>
public sealed record ConfigOverrides
{
    /// <summary>Replaces <c>app.version</c> (<c>--version</c>). It must be a Semantic Versioning 2.0.0 version.</summary>
    public string? Version { get; init; }
}
