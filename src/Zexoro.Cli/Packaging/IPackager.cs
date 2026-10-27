using Zexoro.Configuration;
using Zexoro.Platforms;

namespace Zexoro.Cli.Packaging;

/// <summary>What a packager gets for one <c>pack</c> run.</summary>
internal sealed record PackContext
{
    public required ZexoroConfig Config { get; init; }

    /// <summary>The target platform of the build output; its operating system matches the packager's format.</summary>
    public required TargetPlatform Target { get; init; }

    /// <summary>The directory artifacts are written to; created if missing.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>The release notes URL for release fragments (<c>--notes</c>), already validated, or null.</summary>
    public string? Notes { get; init; }

    /// <summary>Receives progress messages and the created files.</summary>
    public required TextWriter Log { get; init; }

    public required CliEnvironment Environment { get; init; }
}

/// <summary>
/// Builds one package format. Implementations are listed in <see cref="Packagers.All"/>; there is
/// no plugin loading, so the set stays Native AOT safe.
/// </summary>
internal interface IPackager
{
    /// <summary>The format this packager builds.</summary>
    PackageFormat Format { get; }

    /// <summary>Builds the package and, with <c>[updates]</c>, its release fragment.</summary>
    /// <exception cref="PackException">Packing failed; the message is for the user.</exception>
    /// <exception cref="ConfigurationException">A configuration value cannot be resolved.</exception>
    void Pack(PackContext context);
}

/// <summary>A packager failed; the message is shown to the user as is.</summary>
internal sealed class PackException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>The packagers of this build, by format.</summary>
internal static class Packagers
{
    /// <summary>Every implemented packager. Formats of <see cref="PackageFormat.All"/> without one are "not implemented yet".</summary>
    public static IReadOnlyList<IPackager> All { get; } = [new NsisFormatPackager()];

    /// <summary>The packager for <paramref name="format"/>, or null when the format is not implemented yet.</summary>
    public static IPackager? Find(PackageFormat format) => All.FirstOrDefault(p => p.Format == format);
}
