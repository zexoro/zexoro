using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using Zexoro.Formats.Nsis;
using Zexoro.Platforms;

namespace Zexoro.Cli.Toolsets;

/// <summary>A zexoro-updater build that is available to this zexoro, embedded or from <c>ZEXORO_UPDATER_DIR</c>.</summary>
/// <param name="Runtime">The build.</param>
/// <param name="Source">Where it comes from, for messages.</param>
/// <param name="Sha256">The SHA-256 of the executable, as 64 lower-case hex digits.</param>
/// <param name="Open">Opens the (uncompressed) executable for reading.</param>
internal sealed record AvailableUpdater(UpdaterRuntime Runtime, string Source, string Sha256, Func<Stream> Open);

/// <summary>No updater for a runtime identifier is available, or the one found is not for it.</summary>
internal sealed class UpdaterUnavailableException(string message) : Exception(message);

/// <summary>
/// The zexoro-updater executables embedded into this build (see <c>eng/Updater.targets</c>):
/// resource <c>Zexoro.Updater.&lt;rid&gt;.gz</c> (gzip) plus assembly metadata
/// <c>Zexoro.Updater.&lt;rid&gt;.Sha256</c> (of the uncompressed file). <c>ZEXORO_UPDATER_DIR</c>
/// overrides them with a directory of <c>zexoro-updater-&lt;rid&gt;[.exe]</c> files.
/// </summary>
internal static class BundledUpdaters
{
    private const string Prefix = "Zexoro.Updater.";

    /// <summary>Returns the embedded updater for <paramref name="runtime"/>, or null when this build has none.</summary>
    public static AvailableUpdater? FindEmbedded(UpdaterRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var assembly = typeof(BundledUpdaters).Assembly;
        var resource = $"{Prefix}{runtime.Rid}.gz";
        var sha256 = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(m => m.Key == $"{Prefix}{runtime.Rid}.Sha256")?.Value;
        if (sha256 is null || assembly.GetManifestResourceInfo(resource) is null)
        {
            return null;
        }

        return new AvailableUpdater(runtime, $"bundled updater for {runtime.Rid}", sha256, () =>
            new GZipStream(
                assembly.GetManifestResourceStream(resource) ?? throw new UpdaterUnavailableException($"The embedded updater for {runtime.Rid} is missing."),
                CompressionMode.Decompress));
    }

    /// <summary>Returns the embedded updater for a Windows <paramref name="architecture"/>, or null when this build has none.</summary>
    public static UpdaterBinary? FindEmbedded(Architecture architecture) =>
        UpdaterBinary.RuntimeFor(architecture) is { } runtime && FindEmbedded(runtime) is { } updater ? ToBinary(architecture, updater) : null;

    /// <summary>
    /// Returns the updater for <paramref name="runtime"/>: from <c>ZEXORO_UPDATER_DIR</c> when it is
    /// set, otherwise the embedded one.
    /// </summary>
    /// <exception cref="UpdaterUnavailableException">No updater for the runtime is available, or the file found is for another platform.</exception>
    /// <exception cref="IOException">The file in <c>ZEXORO_UPDATER_DIR</c> cannot be read.</exception>
    public static AvailableUpdater Resolve(UpdaterRuntime runtime, CliEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.GetVariable(UpdaterRuntime.DirectoryVariable) is { Length: > 0 } directory)
        {
            var path = Path.Combine(Path.GetFullPath(directory, environment.WorkingDirectory), runtime.DirectoryFileName);
            if (!File.Exists(path))
            {
                throw new UpdaterUnavailableException($"{UpdaterRuntime.DirectoryVariable} is set, but {path} does not exist.");
            }

            // The header names the operating system and architecture; glibc and musl builds look alike.
            var target = ExecutableInspector.Inspect(path).Target;
            if (target != runtime.Platform)
            {
                throw new UpdaterUnavailableException($"{path} is not a {Describe(runtime.Platform)} executable.");
            }

            string sha256;
            using (var stream = File.OpenRead(path))
            {
                sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
            }

            return new AvailableUpdater(runtime, $"updater for {runtime.Rid} from {UpdaterRuntime.DirectoryVariable} ({path})", sha256, () => File.OpenRead(path));
        }

        return FindEmbedded(runtime) ?? throw new UpdaterUnavailableException(
            $"This zexoro build has no embedded updater for {runtime.Rid}, which packages with [updates] need. "
            + $"Set {UpdaterRuntime.DirectoryVariable} to a directory with {runtime.DirectoryFileName} "
            + $"(on {Describe(runtime.Platform.OperatingSystem)}: dotnet publish src/Zexoro.Updater -c Release -r {runtime.Rid}).");
    }

    /// <summary>
    /// Returns the updater for a Windows <paramref name="architecture"/>: from <c>ZEXORO_UPDATER_DIR</c>
    /// when it is set, otherwise the embedded one.
    /// </summary>
    /// <exception cref="NsisException">No updater for the architecture is available.</exception>
    public static UpdaterBinary Resolve(Architecture architecture, CliEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (UpdaterBinary.RuntimeFor(architecture) is not { } runtime)
        {
            throw new NsisException($"zexoro-updater is not built for {UpdaterBinary.RidFor(architecture)}.");
        }

        try
        {
            return ToBinary(architecture, Resolve(runtime, environment));
        }
        catch (UpdaterUnavailableException ex)
        {
            throw new NsisException(ex.Message);
        }
    }

    /// <summary>Writes the updater for a Windows <paramref name="architecture"/> to <c>&lt;directory&gt;/zexoro-updater.exe</c> and returns its path.</summary>
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

    private static UpdaterBinary ToBinary(Architecture architecture, AvailableUpdater updater) =>
        new(architecture, updater.Source, updater.Sha256, updater.Open);

    private static string Describe(TargetPlatform platform) => $"{Describe(platform.OperatingSystem)} {platform.Architecture.Name}";

    private static string Describe(OperatingSystemFamily os) =>
        os == OperatingSystemFamily.Windows ? "Windows" : os == OperatingSystemFamily.MacOS ? "macOS" : os == OperatingSystemFamily.Linux ? "Linux" : os.Name;
}
