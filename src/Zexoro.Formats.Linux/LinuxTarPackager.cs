using Zexoro.Configuration;
using Zexoro.Formats.Nsis;
using Zexoro.Platforms;

namespace Zexoro.Formats.Linux;

/// <summary>Packing a Linux format (tar.gz, deb) failed; the message is for the user.</summary>
public sealed class LinuxPackException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>What <see cref="LinuxTarPackager.Pack"/> needs.</summary>
public sealed record LinuxTarPackRequest
{
    public required ZexoroConfig Config { get; init; }

    /// <summary>The target architecture (x64 or arm64).</summary>
    public required Architecture Architecture { get; init; }

    /// <summary>The directory the archive is written to; created if missing.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Receives progress messages and warnings; null for none.</summary>
    public TextWriter? Log { get; init; }

    /// <summary>
    /// Returns the zexoro-updater build for a runtime; needed with <c>[updates]</c>. It throws
    /// <see cref="LinuxPackException"/> when none is available.
    /// </summary>
    public Func<UpdaterRuntime, UnixUpdaterBinary>? ResolveUpdater { get; init; }
}

/// <summary>The result of <see cref="LinuxTarPackager.Pack"/>.</summary>
/// <param name="ArchivePath">The archive that was written.</param>
/// <param name="Architecture">Its architecture.</param>
public sealed record LinuxTarPackResult(string ArchivePath, Architecture Architecture);

/// <summary>
/// Linux <c>tar.gz</c> (docs/design/0006-desktop-formats.md section 2): a portable archive
/// <c>&lt;name&gt;-&lt;version&gt;-linux-&lt;arch&gt;.tar.gz</c> with one top-level directory
/// <c>&lt;name&gt;/</c> holding <c>app/</c>, <c>zexoro-install.json</c>, <c>icon.png</c> and, with
/// <c>[updates]</c>, <c>zexoro-updater</c> (mode 0755). The root is wherever the user extracts it;
/// nothing is installed system-wide. Updates use the <c>replace</c> strategy, as for the Windows zip.
/// </summary>
/// <remarks>
/// The archive always carries the glibc updater of its architecture (<c>linux-x64</c>,
/// <c>linux-arm64</c>): target detection cannot tell glibc from musl builds, and the feed's targets
/// (os, arch, format, scope) could not tell two such archives apart either. musl systems (Alpine) are
/// served by the apk format with the musl updater; packing a main executable linked against musl
/// prints a warning.
/// </remarks>
public static class LinuxTarPackager
{
    /// <summary>The record's and the feed artifact's <c>format</c>; feed tokens allow no '.'.</summary>
    public const string FormatName = "tar-gz";

    /// <summary>A portable archive is always a per-user install.</summary>
    public const string Scope = "user";

    public const string Strategy = "replace";

    public const string Extension = ".tar.gz";

    /// <summary>The icon in the archive's top-level directory.</summary>
    public const string IconFileName = "icon.png";

    /// <summary>
    /// The release fragments' <c>minimumUpdater</c>: 0.2.0 (any prerelease of it) is the first
    /// zexoro-updater built for Linux, and it applies tar.gz archives. Raise it when the archive
    /// contract changes.
    /// </summary>
    public const string MinimumUpdater = "0.2.0-0";

    /// <summary>The architectures a tar.gz is built for.</summary>
    public static IReadOnlyList<Architecture> Architectures { get; } = [Architecture.X64, Architecture.Arm64];

    /// <summary>The archive file name, for example <c>ExampleNotes-1.2.3-linux-x64.tar.gz</c>.</summary>
    public static string GetOutputFileName(AppConfig app, Architecture architecture)
    {
        ArgumentNullException.ThrowIfNull(app);
        return $"{WindowsPayload.ArtifactName(app)}-{app.Version}-linux-{architecture.Name}{Extension}";
    }

    /// <summary>The top-level directory in the archive, for example <c>ExampleNotes</c>.</summary>
    public static string GetRootFolderName(AppConfig app) => WindowsPayload.ArtifactName(app);

    /// <summary>
    /// The application icon as PNG: <c>[linux] icon</c> (a PNG file), else the largest image of
    /// <c>[app] icon</c> (PNG or ICO).
    /// </summary>
    /// <exception cref="LinuxPackException">The icon is missing or not usable.</exception>
    public static PngImage LoadIcon(ZexoroConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var path = config.Linux.Icon ?? config.App.Icon;
        if (!File.Exists(path.FullPath))
        {
            throw new LinuxPackException($"{path.Key}: {path.FullPath} not found.");
        }

        try
        {
            var bytes = File.ReadAllBytes(path.FullPath);
            if (config.Linux.Icon is not null && !PngIcon.IsPng(bytes))
            {
                throw new LinuxPackException($"{path.Key}: {path.FullPath} is not a PNG file.");
            }

            return PngIcon.FromBytes(bytes);
        }
        catch (IconException ex)
        {
            throw new LinuxPackException($"{path.Key}: {path.FullPath}: {ex.Message}.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LinuxPackException($"{path.Key}: cannot read {path.FullPath}: {ex.Message}", ex);
        }
    }

    /// <summary>Builds the archive.</summary>
    /// <exception cref="LinuxPackException">An input is missing or invalid, or the archive cannot be written.</exception>
    public static LinuxTarPackResult Pack(LinuxTarPackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = request.Config;
        var input = config.Input;
        var architecture = request.Architecture;
        if (!Architectures.Contains(architecture))
        {
            throw new LinuxPackException($"Linux tar.gz archives support x64 and arm64, not {architecture.Name}.");
        }

        if (!Directory.Exists(input.Directory.FullPath))
        {
            throw new LinuxPackException($"input.dir: directory {input.Directory.FullPath} not found; build your application first.");
        }

        if (!File.Exists(input.Main.FullPath))
        {
            throw new LinuxPackException($"input.main: {input.Main.FullPath} not found.");
        }

        var icon = LoadIcon(config);
        var (updater, updates) = ReadUpdater(config, architecture, request.ResolveUpdater, request.Log);
        WarnAboutMusl(input.Main.FullPath, request.Log);
        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        var rootName = GetRootFolderName(config.App);
        try
        {
            var items = new List<TarItem> { TarItem.Directory(rootName), TarItem.Directory(rootName + "/app") };
            items.AddRange(LinuxPayload.Read(input.Directory.FullPath, rootName + "/app", input.Main.Value, outputDirectory));
            var record = InstallRecord.Create(
                config.App.Id, config.App.Version, new TargetPlatform(OperatingSystemFamily.Linux, architecture), FormatName, Scope, Strategy, updates, input.Main.Value);
            items.Add(TarItem.FromBytes(rootName + "/zexoro-install.json", record, TarItem.Regular));
            items.Add(TarItem.FromBytes(rootName + "/" + IconFileName, icon.Bytes, TarItem.Regular));
            if (updater is not null)
            {
                items.Add(TarItem.FromBytes(rootName + "/zexoro-updater", updater, TarItem.Executable));
            }

            Directory.CreateDirectory(outputDirectory);
            var archive = Path.Combine(outputDirectory, GetOutputFileName(config.App, architecture));
            var temporary = archive + ".tmp";
            request.Log?.WriteLine($"Writing {Path.GetFileName(archive)}");
            try
            {
                ReproducibleTar.Write(items, temporary);
                File.Move(temporary, archive, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }

            return new LinuxTarPackResult(archive, architecture);
        }
        catch (PayloadException ex)
        {
            throw new LinuxPackException(ex.Message, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LinuxPackException($"Cannot build the archive: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// With <c>[updates]</c>, reads the updater of <paramref name="architecture"/> (glibc, or musl
    /// when <paramref name="musl"/> is set) and resolves
    /// the update settings for the install record; both null without <c>[updates]</c>. Called before
    /// anything is written, so a missing updater or key file fails fast.
    /// </summary>
    /// <exception cref="LinuxPackException">The updater is not available or not valid.</exception>
    internal static (byte[]? Updater, NsisUpdates? Updates) ReadUpdater(
        ZexoroConfig config, Architecture architecture, Func<UpdaterRuntime, UnixUpdaterBinary>? resolve, TextWriter? log, bool musl = false)
    {
        if (config.Updates is not { } updatesConfig)
        {
            return (null, null);
        }

        var runtime = UpdaterRuntime.Find(new TargetPlatform(OperatingSystemFamily.Linux, architecture), musl)!;
        var binary = resolve?.Invoke(runtime)
            ?? throw new LinuxPackException($"[updates] is configured, but no zexoro-updater for {runtime.Rid} is available.");
        byte[] updater;
        try
        {
            log?.WriteLine($"Adding zexoro-updater ({binary.Source})");
            updater = binary.ReadVerified();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new LinuxPackException($"Cannot read the updater for {runtime.Rid} ({binary.Source}): {ex.Message}", ex);
        }

        return (updater, new NsisUpdates(updatesConfig.Channel, updatesConfig.FeedUrl, updatesConfig.ResolveKeys()));
    }

    /// <summary>Warns when the main executable needs musl's dynamic loader: the archive's glibc updater does not run on musl systems.</summary>
    private static void WarnAboutMusl(string main, TextWriter? log)
    {
        string? interpreter;
        try
        {
            interpreter = ExecutableInspector.ReadElfInterpreter(main);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (interpreter is not null && interpreter.Contains("ld-musl", StringComparison.Ordinal))
        {
            log?.WriteLine(
                $"warning: input.main is linked against musl ({interpreter}), but tar.gz archives are for glibc distributions "
                + "and carry the glibc zexoro-updater; Alpine and other musl systems are served by the apk format.");
        }
    }
}
