using System.Reflection;
using System.Security.Cryptography;
using Zexoro.Formats.Nsis;
using Zexoro.Platforms;

namespace Zexoro.Cli.Toolsets;

/// <summary>
/// The zexoro-updater executables embedded into this build (see <c>eng/Updater.targets</c>):
/// resource <c>Zexoro.Updater.&lt;rid&gt;.exe</c> plus assembly metadata <c>Zexoro.Updater.&lt;rid&gt;.Sha256</c>.
/// <c>ZEXORO_UPDATER_DIR</c> overrides them with a directory of <c>zexoro-updater-&lt;rid&gt;.exe</c> files.
/// </summary>
internal static class BundledUpdaters
{
    private const string Prefix = "Zexoro.Updater.";

    /// <summary>Returns the embedded updater for <paramref name="architecture"/>, or null when this build has none.</summary>
    public static UpdaterBinary? FindEmbedded(Architecture architecture)
    {
        var assembly = typeof(BundledUpdaters).Assembly;
        var rid = UpdaterBinary.RidFor(architecture);
        var resource = $"{Prefix}{rid}.exe";
        var sha256 = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(m => m.Key == $"{Prefix}{rid}.Sha256")?.Value;
        if (sha256 is null || assembly.GetManifestResourceInfo(resource) is null)
        {
            return null;
        }

        return new UpdaterBinary(architecture, $"bundled updater for {rid}", sha256, () =>
            assembly.GetManifestResourceStream(resource) ?? throw new NsisException($"The embedded updater for {rid} is missing."));
    }

    /// <summary>
    /// Returns the updater for <paramref name="architecture"/>: from <c>ZEXORO_UPDATER_DIR</c> when it is
    /// set, otherwise the embedded one.
    /// </summary>
    /// <exception cref="NsisException">No updater for the architecture is available.</exception>
    public static UpdaterBinary Resolve(Architecture architecture, CliEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var rid = UpdaterBinary.RidFor(architecture);
        if (!UpdaterBinary.Architectures.Contains(architecture))
        {
            throw new NsisException($"zexoro-updater is not built for {rid}.");
        }

        if (environment.GetVariable(UpdaterBinary.DirectoryVariable) is { Length: > 0 } directory)
        {
            var path = Path.Combine(Path.GetFullPath(directory, environment.WorkingDirectory), UpdaterBinary.FileNameFor(architecture));
            if (!File.Exists(path))
            {
                throw new NsisException($"{UpdaterBinary.DirectoryVariable} is set, but {path} does not exist.");
            }

            if (PeMachine.Read(path) != architecture)
            {
                throw new NsisException($"{path} is not a Windows {architecture.Name} executable.");
            }

            string sha256;
            using (var stream = File.OpenRead(path))
            {
                sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
            }

            return new UpdaterBinary(architecture, $"updater for {rid} from {UpdaterBinary.DirectoryVariable} ({path})", sha256, () => File.OpenRead(path));
        }

        return FindEmbedded(architecture) ?? throw new NsisException(
            $"This zexoro build has no embedded updater for {rid}, which installers with [updates] need. "
            + $"Set {UpdaterBinary.DirectoryVariable} to a directory with {UpdaterBinary.FileNameFor(architecture)} "
            + "(on Windows: dotnet publish src/Zexoro.Updater -c Release -r " + rid + ").");
    }

    /// <summary>Writes the updater for <paramref name="architecture"/> to <c>&lt;directory&gt;/zexoro-updater.exe</c> and returns its path.</summary>
    /// <exception cref="NsisException">No matching updater is available.</exception>
    /// <exception cref="IOException">It cannot be written.</exception>
    public static string Extract(Architecture architecture, CliEnvironment environment, string directory)
    {
        var updater = Resolve(architecture, environment);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, UpdaterBinary.FileName);
        updater.WriteTo(path);
        return path;
    }
}
