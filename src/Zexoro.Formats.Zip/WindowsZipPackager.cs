using Zexoro.Configuration;
using Zexoro.Formats.Nsis;
using Zexoro.Platforms;
using Zexoro.Signing;

namespace Zexoro.Formats.Zip;

/// <summary>Packing a portable zip failed; the message is for the user.</summary>
public sealed class ZipPackException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>What <see cref="WindowsZipPackager.Pack"/> needs.</summary>
public sealed record WindowsZipPackRequest
{
    public required ZexoroConfig Config { get; init; }

    /// <summary>The target architecture (x86, x64 or arm64).</summary>
    public required Architecture Architecture { get; init; }

    /// <summary>The directory the archive is written to; created if missing.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Signing settings (only the credential and timestamp are used), or null to pack unsigned.</summary>
    public NsisSigning? Signing { get; init; }

    /// <summary>Receives progress messages; null for none.</summary>
    public TextWriter? Log { get; init; }

    /// <summary>
    /// Returns the zexoro-updater for the architecture; needed with <c>[updates]</c>. It throws
    /// <see cref="NsisException"/> when none is available.
    /// </summary>
    public Func<Architecture, UpdaterBinary>? ResolveUpdater { get; init; }
}

/// <summary>The result of <see cref="WindowsZipPackager.Pack"/>.</summary>
/// <param name="ArchivePath">The archive that was written.</param>
/// <param name="Architecture">Its architecture.</param>
/// <param name="SignedFiles">Application files signed before packing, relative to the input directory.</param>
public sealed record WindowsZipPackResult(string ArchivePath, Architecture Architecture, IReadOnlyList<string> SignedFiles);

/// <summary>
/// Windows <c>zip</c> (docs/design/0006-desktop-formats.md section 2): a portable archive
/// <c>&lt;name&gt;-&lt;version&gt;-win-&lt;arch&gt;.zip</c> with one top-level folder <c>&lt;name&gt;\</c>
/// holding <c>app\</c>, <c>zexoro-install.json</c> and, with <c>[updates]</c>, <c>zexoro-updater.exe</c>.
/// The root is wherever the user extracts it; nothing is registered. Updates use the
/// <c>replace</c> strategy: the updater extracts the next archive next to the install and swaps it in.
/// </summary>
public static class WindowsZipPackager
{
    /// <summary>The record's and the feed artifact's <c>format</c>.</summary>
    public const string FormatName = "zip";

    /// <summary>A portable zip is always a per-user install: it never elevates.</summary>
    public const string Scope = "user";

    public const string Strategy = "replace";

    public const string Extension = ".zip";

    /// <summary>
    /// The release fragments' <c>minimumUpdater</c>: 0.2.0 (any prerelease of it) is the first
    /// zexoro-updater that applies zip archives. Raise it when the archive contract changes.
    /// </summary>
    public const string MinimumUpdater = "0.2.0-0";

    /// <summary>The archive file name, for example <c>ExampleNotes-1.2.3-win-x64.zip</c>.</summary>
    public static string GetOutputFileName(AppConfig app, Architecture architecture) =>
        WindowsPayload.ArtifactFileName(app, architecture, Extension);

    /// <summary>The top-level folder in the archive, for example <c>ExampleNotes</c>.</summary>
    public static string GetRootFolderName(AppConfig app) => WindowsPayload.ArtifactName(app);

    /// <summary>Builds the archive.</summary>
    /// <exception cref="ZipPackException">An input is missing or invalid, signing fails, or the archive cannot be written.</exception>
    public static WindowsZipPackResult Pack(WindowsZipPackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = request.Config;
        var input = config.Input;
        var architecture = request.Architecture;
        if (!UpdaterBinary.Architectures.Contains(architecture))
        {
            throw new ZipPackException($"Windows zip archives support x86, x64 and arm64, not {architecture.Name}.");
        }

        if (!Directory.Exists(input.Directory.FullPath))
        {
            throw new ZipPackException($"input.dir: directory {input.Directory.FullPath} not found; build your application first.");
        }

        if (!File.Exists(input.Main.FullPath))
        {
            throw new ZipPackException($"input.main: {input.Main.FullPath} not found.");
        }

        UpdaterBinary? updater = null;
        NsisUpdates? updates = null;
        try
        {
            if (config.Updates is { } updatesConfig)
            {
                // Both before anything is staged, so a missing updater or key file fails fast.
                updater = request.ResolveUpdater?.Invoke(architecture)
                    ?? throw new ZipPackException($"[updates] is configured, but no zexoro-updater for {UpdaterBinary.RidFor(architecture)} is available.");
                updates = new NsisUpdates(updatesConfig.Channel, updatesConfig.FeedUrl, updatesConfig.ResolveKeys());
            }
        }
        catch (NsisException ex)
        {
            throw new ZipPackException(ex.Message, ex);
        }

        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        var staging = Directory.CreateTempSubdirectory("zexoro-zip-").FullName;
        try
        {
            var appDirectory = Path.Combine(staging, "app");
            WindowsPayload.CopyDirectory(input.Directory.FullPath, appDirectory, outputDirectory);
            var signed = request.Signing is { } signing
                ? WindowsPayload.SignApplicationFiles(appDirectory, input.Main.Value, config.Windows.Sign, signing, config.App, request.Log)
                : [];
            if (updater is not null)
            {
                WindowsPayload.StageUpdater(updater, Path.Combine(staging, UpdaterBinary.FileName), request.Signing, config.App, request.Log);
            }

            File.WriteAllBytes(
                Path.Combine(staging, "zexoro-install.json"),
                InstallRecord.Create(config.App.Id, config.App.Version, architecture, FormatName, Scope, Strategy, updates, input.Main.Value));

            Directory.CreateDirectory(outputDirectory);
            var archive = Path.Combine(outputDirectory, GetOutputFileName(config.App, architecture));
            var temporary = archive + ".tmp";
            request.Log?.WriteLine($"Writing {Path.GetFileName(archive)}");
            try
            {
                ReproducibleZip.WriteDirectory(staging, GetRootFolderName(config.App), temporary);
                File.Move(temporary, archive, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }

            return new WindowsZipPackResult(archive, architecture, signed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new ZipPackException($"Cannot build the archive: {ex.Message}", ex);
        }
        catch (SigningException ex)
        {
            throw new ZipPackException($"Signing failed: {ex.Message}", ex);
        }
        catch (NsisException ex)
        {
            throw new ZipPackException(ex.Message, ex);
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                request.Log?.WriteLine($"note: could not delete the temporary directory {staging}: {ex.Message}");
            }
        }
    }
}
