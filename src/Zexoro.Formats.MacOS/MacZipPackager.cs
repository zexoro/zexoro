using Zexoro.Configuration;
using Zexoro.Formats.Linux;
using Zexoro.Formats.Nsis;
using Zexoro.Formats.Zip;
using Zexoro.Platforms;

namespace Zexoro.Formats.MacOS;

/// <summary>What <see cref="MacZipPackager.Pack"/> needs.</summary>
public sealed record MacZipPackRequest
{
    public required ZexoroConfig Config { get; init; }

    /// <summary>The target architecture: x64, arm64 or universal.</summary>
    public required Architecture Architecture { get; init; }

    /// <summary>The directory the zip is written to; created if missing.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Runs codesign, notarytool and stapler.</summary>
    public required IMacToolRunner Tools { get; init; }

    /// <summary>The signing identity, or null to sign ad hoc.</summary>
    public MacSigningIdentity? Signing { get; init; }

    /// <summary>The notarization key, or null to skip notarization.</summary>
    public MacNotarizationKey? Notarization { get; init; }

    /// <summary>Returns a zexoro-updater build (osx-x64 or osx-arm64); needed with <c>[updates]</c>.</summary>
    public Func<UpdaterRuntime, UnixUpdaterBinary>? ResolveUpdater { get; init; }

    public TextWriter? Log { get; init; }
}

/// <summary>The result of <see cref="MacZipPackager.Pack"/>.</summary>
/// <param name="ZipPath">The zip that was written.</param>
/// <param name="Architecture">Its architecture.</param>
/// <param name="Notarized">Whether the bundle was notarized and stapled.</param>
public sealed record MacZipPackResult(string ZipPath, Architecture Architecture, bool Notarized);

/// <summary>
/// macOS <c>zip</c> (docs/design/0006-desktop-formats.md sections 1 and 2):
/// <c>&lt;name&gt;-&lt;version&gt;-osx-&lt;arch&gt;.zip</c> holding the signed (and, when configured,
/// notarized and stapled) <c>&lt;app.name&gt;.app</c> with Unix modes and symbolic links, as
/// <c>ditto -c -k --keepParent</c> lays it out. This zip is also the update payload of the
/// <c>bundle</c> strategy for every macOS container.
/// </summary>
public static class MacZipPackager
{
    public const string Extension = ".zip";

    /// <summary>
    /// The release fragments' <c>minimumUpdater</c>: 0.2.0 (any prerelease of it) is the first
    /// zexoro-updater built for macOS; the <c>bundle</c> strategy lands before 0.2.0 is released
    /// (stage 5 step 10), so every released updater that reads a macOS record can apply it.
    /// </summary>
    public const string MinimumUpdater = "0.2.0-0";

    /// <summary>The architectures a macOS bundle is built for.</summary>
    public static IReadOnlyList<Architecture> Architectures { get; } = [Architecture.X64, Architecture.Arm64, Architecture.Universal];

    /// <summary>The zip file name, for example <c>ExampleNotes-1.2.3-osx-universal.zip</c>.</summary>
    public static string GetOutputFileName(AppConfig app, Architecture architecture)
    {
        ArgumentNullException.ThrowIfNull(app);
        return $"{WindowsPayload.ArtifactName(app)}-{app.Version}-osx-{architecture.Name}{Extension}";
    }

    /// <summary>
    /// The application icon as icns: <c>[macos] icon</c> when it is an icns file (used as it is) or
    /// a PNG, else the largest image of <c>[app] icon</c> (PNG or ICO), as one PNG element.
    /// </summary>
    /// <exception cref="MacPackException">The icon is missing or not usable.</exception>
    public static byte[] LoadIcon(ZexoroConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var path = config.MacOS.Icon ?? config.App.Icon;
        if (!File.Exists(path.FullPath))
        {
            throw new MacPackException($"{path.Key}: {path.FullPath} not found.");
        }

        try
        {
            var bytes = File.ReadAllBytes(path.FullPath);
            if (config.MacOS.Icon is not null && bytes.AsSpan().StartsWith("icns"u8))
            {
                return Icns.IsIcns(bytes) ? bytes : throw new MacPackException($"{path.Key}: {path.FullPath} is a damaged icns file.");
            }

            if (config.MacOS.Icon is not null && !PngIcon.IsPng(bytes))
            {
                throw new MacPackException($"{path.Key}: {path.FullPath} is neither an icns nor a PNG file.");
            }

            return Icns.Create([PngIcon.FromBytes(bytes)]);
        }
        catch (IconException ex)
        {
            var hint = config.MacOS.Icon is null ? "; set [macos] icon to an .icns file or a square PNG" : string.Empty;
            throw new MacPackException($"{path.Key}: {path.FullPath}: {ex.Message}{hint}.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MacPackException($"{path.Key}: cannot read {path.FullPath}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The updater for <paramref name="architecture"/>: the thin osx-x64 or osx-arm64 build, or for
    /// universal bundles a universal file written from both.
    /// </summary>
    /// <exception cref="MacPackException">An updater is not available or not valid.</exception>
    public static byte[] ReadUpdater(Architecture architecture, Func<UpdaterRuntime, UnixUpdaterBinary>? resolve, TextWriter? log)
    {
        byte[] Read(UpdaterRuntime runtime)
        {
            var binary = resolve?.Invoke(runtime)
                ?? throw new MacPackException($"[updates] is configured, but no zexoro-updater for {runtime.Rid} is available.");
            try
            {
                log?.WriteLine($"Adding zexoro-updater ({binary.Source})");
                return binary.ReadVerified();
            }
            catch (LinuxPackException ex)
            {
                throw new MacPackException(ex.Message, ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                throw new MacPackException($"Cannot read the updater for {runtime.Rid} ({binary.Source}): {ex.Message}", ex);
            }
        }

        if (architecture == Architecture.X64)
        {
            return Read(UpdaterRuntime.OsxX64);
        }

        if (architecture == Architecture.Arm64)
        {
            return Read(UpdaterRuntime.OsxArm64);
        }

        try
        {
            return FatMachO.Create([Read(UpdaterRuntime.OsxX64), Read(UpdaterRuntime.OsxArm64)]);
        }
        catch (ArgumentException ex)
        {
            throw new MacPackException($"Cannot make a universal zexoro-updater: {ex.Message}", ex);
        }
    }

    /// <summary>Builds, signs, notarizes (when configured) and zips the bundle.</summary>
    /// <exception cref="MacPackException">An input is missing or invalid, or a tool fails.</exception>
    public static MacZipPackResult Pack(MacZipPackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = request.Config;
        var architecture = request.Architecture;
        var log = request.Log;
        if (!Architectures.Contains(architecture))
        {
            throw new MacPackException($"macOS bundles support x64, arm64 and universal, not {architecture.Name}.");
        }

        if (!Directory.Exists(config.Input.Directory.FullPath))
        {
            throw new MacPackException($"input.dir: directory {config.Input.Directory.FullPath} not found; build your application first.");
        }

        if (config.Updates is not null && request.Signing is null)
        {
            throw new MacPackException(
                "[updates] needs [macos.signing] for macOS: an update must satisfy the installed bundle's designated requirement, "
                + "and an ad-hoc signature's requirement is its own hash, which no other version can satisfy. "
                + "Configure a signing identity (a Developer ID Application certificate to distribute), or remove [updates].");
        }

        if (request.Notarization is not null && request.Signing is null)
        {
            throw new MacPackException("[macos.notarization] needs [macos.signing]: Apple notarizes only code signed with a Developer ID certificate.");
        }

        if (request.Signing is null)
        {
            log?.WriteLine("notice: [macos.signing] is not configured; the bundle is signed ad hoc, which runs on this Mac but not on others without "
                + "the user's override (Gatekeeper), and cannot be updated.");
        }

        if (request.Signing?.Entitlements is { } entitlements && !File.Exists(entitlements))
        {
            throw new MacPackException($"macos.signing.entitlements: {entitlements} not found.");
        }

        if (request.Notarization is { } key && !File.Exists(key.KeyPath))
        {
            throw new MacPackException($"macos.notarization.key: {key.KeyPath} not found.");
        }

        byte[]? updater = null;
        NsisUpdates? updates = null;
        if (config.Updates is { } updatesConfig)
        {
            updater = ReadUpdater(architecture, request.ResolveUpdater, log);
            updates = new NsisUpdates(updatesConfig.Channel, updatesConfig.FeedUrl, updatesConfig.ResolveKeys());
        }

        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        var zip = Path.Combine(outputDirectory, GetOutputFileName(config.App, architecture));
        var staging = Path.Combine(outputDirectory, $".{Path.GetFileName(zip)}.{Guid.NewGuid():N}.staging");
        var temporary = zip + ".tmp";
        try
        {
            Directory.CreateDirectory(staging);
            var bundle = MacBundle.Create(new MacBundleRequest
            {
                Config = config,
                Architecture = architecture,
                StagingDirectory = staging,
                Exclude = outputDirectory,
                Updater = updater,
                Updates = updates,
                LoadIcon = () => LoadIcon(config),
                Log = log,
            });
            log?.WriteLine(bundle.Generated
                ? $"Created {Path.GetFileName(bundle.Path)} from {config.Input.Directory.FullPath}"
                : $"Copied {Path.GetFileName(bundle.Path)} from {config.Input.Directory.FullPath}");
            MacCodeSigner.Sign(request.Tools, bundle.Path, bundle.MainExecutable, request.Signing, log);

            log?.WriteLine($"Writing {Path.GetFileName(zip)}");
            var prefix = Path.GetFileName(bundle.Path);
            UnixZip.WriteDirectory(bundle.Path, prefix, temporary);
            var notarized = false;
            if (request.Notarization is { } notarization)
            {
                MacNotarizer.Notarize(request.Tools, temporary, bundle.Path, notarization, log);
                MacCodeSigner.Verify(request.Tools, bundle.Path);
                UnixZip.WriteDirectory(bundle.Path, prefix, temporary);
                notarized = true;
            }
            else if (request.Signing is not null)
            {
                log?.WriteLine("notice: [macos.notarization] is not configured; the bundle is not notarized, so Gatekeeper blocks it on other Macs "
                    + "until it is notarized.");
            }

            File.Move(temporary, zip, overwrite: true);
            return new MacZipPackResult(zip, architecture, notarized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MacPackException($"Cannot build the bundle: {ex.Message}", ex);
        }
        finally
        {
            File.Delete(temporary);
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }
}
