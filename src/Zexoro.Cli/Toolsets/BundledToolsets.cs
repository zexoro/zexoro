using System.Reflection;
using Zexoro.Formats.Nsis;
using Zexoro.Toolsets;

namespace Zexoro.Cli.Toolsets;

/// <summary>
/// The toolset archives embedded into this build (see <c>eng/toolsets.json</c> and
/// <c>eng/Toolsets.targets</c>): resource <c>Zexoro.Toolsets.&lt;name&gt;.tar.gz</c> plus assembly
/// metadata <c>Zexoro.Toolsets.&lt;name&gt;.Version</c> and <c>.Sha256</c>.
/// </summary>
internal static class BundledToolsets
{
    private const string Prefix = "Zexoro.Toolsets.";

    /// <summary>Returns the embedded archive called <paramref name="name"/>, or null when this build has none.</summary>
    public static ToolsetArchive? Find(string name)
    {
        var assembly = typeof(BundledToolsets).Assembly;
        var resource = $"{Prefix}{name}.tar.gz";
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToList();
        var version = metadata.FirstOrDefault(m => m.Key == $"{Prefix}{name}.Version")?.Value;
        var sha256 = metadata.FirstOrDefault(m => m.Key == $"{Prefix}{name}.Sha256")?.Value;
        if (version is null || sha256 is null || assembly.GetManifestResourceInfo(resource) is null)
        {
            return null;
        }

        return new ToolsetArchive(name, version, sha256, () =>
            assembly.GetManifestResourceStream(resource) ?? throw new ToolsetException($"The embedded {name} toolset is missing."));
    }

    /// <summary>
    /// Returns the NSIS toolset: the directory in <c>ZEXORO_NSIS_DIR</c> if set, otherwise the
    /// bundled archive, extracted into the cache and verified.
    /// </summary>
    /// <exception cref="NsisException">No usable toolset is available.</exception>
    /// <exception cref="ToolsetException">The bundled toolset cannot be extracted.</exception>
    public static NsisToolset ResolveNsis(CliEnvironment environment)
    {
        if (environment.GetVariable(NsisToolset.DirectoryVariable) is { Length: > 0 } directory)
        {
            return NsisToolset.Open(
                Path.GetFullPath(directory, environment.WorkingDirectory),
                $"NSIS from {NsisToolset.DirectoryVariable} ({directory})");
        }

        var archive = Find(NsisToolset.Name) ?? throw new NsisException(
            $"This zexoro build has no bundled NSIS toolset for {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}. "
            + $"Set {NsisToolset.DirectoryVariable} to an NSIS 3.13 directory (with Bin/makensis).");
        var extracted = ToolsetCache.Install(archive, ToolsetCache.GetDefaultRoot(environment.GetVariable), NsisToolset.ExtractFilter);
        return NsisToolset.Open(extracted, $"bundled NSIS {archive.Version}");
    }
}
