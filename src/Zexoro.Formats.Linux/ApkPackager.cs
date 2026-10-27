using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Zexoro.Configuration;
using Zexoro.Formats.Nsis;
using Zexoro.Platforms;

namespace Zexoro.Formats.Linux;

/// <summary>Signs apk control segments; made by the caller from <c>[linux.apk.signing]</c>.</summary>
/// <param name="KeyName">The public key's file name in <c>/etc/apk/keys</c> (<c>key_name</c>).</param>
/// <param name="Description">The key, for the log.</param>
/// <param name="Sign">Returns the RSA PKCS #1 v1.5 signature with SHA-256 over the compressed control segment.</param>
public sealed record ApkSigner(string KeyName, string Description, Func<byte[], byte[]> Sign);

/// <summary>What <see cref="ApkPackager.Pack"/> needs.</summary>
public sealed record ApkPackRequest
{
    public required ZexoroConfig Config { get; init; }

    /// <summary>The target architecture (x64 or arm64).</summary>
    public required Architecture Architecture { get; init; }

    /// <summary>The directory the package is written to; created if missing.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Receives progress messages and notices; null for none.</summary>
    public TextWriter? Log { get; init; }

    /// <summary>
    /// Returns the zexoro-updater build for a runtime (here the musl builds); needed with
    /// <c>[updates]</c>. It throws <see cref="LinuxPackException"/> when none is available.
    /// </summary>
    public Func<UpdaterRuntime, UnixUpdaterBinary>? ResolveUpdater { get; init; }

    /// <summary>Signs the package, or null for an unsigned package.</summary>
    public ApkSigner? Signer { get; init; }
}

/// <summary>The result of <see cref="ApkPackager.Pack"/>.</summary>
/// <param name="PackagePath">The package that was written.</param>
/// <param name="Architecture">Its architecture.</param>
/// <param name="PackageInfo">The <c>.PKGINFO</c> file, as written.</param>
public sealed record ApkPackResult(string PackagePath, Architecture Architecture, string PackageInfo);

/// <summary>
/// Alpine apk packages in the v2 format (docs/design/0006-desktop-formats.md sections 2 and 3,
/// "As implemented (step 8)"), written in .NET: up to three gzip streams that together form one
/// tar archive — the signature segment (<c>.SIGN.RSA256.&lt;key_name&gt;</c>, absent when
/// unsigned), the control segment (<c>.PKGINFO</c>, with the SHA-256 <c>datahash</c> of the data
/// segment) and the data segment — reproducible like the deb and rpm. The layout is theirs:
/// <c>/opt/&lt;pkg&gt;/app/</c>, <c>/opt/&lt;pkg&gt;/zexoro-install.json</c> (format <c>apk</c>,
/// scope <c>machine</c>, strategy <c>notify</c>), with <c>[updates]</c> the musl build of
/// <c>/opt/&lt;pkg&gt;/zexoro-updater</c>, the link <c>/usr/bin/&lt;pkg&gt;</c>, a
/// <c>.desktop</c> file and the icon. No install scripts.
/// </summary>
public static class ApkPackager
{
    /// <summary>The record's and the feed artifact's <c>format</c>.</summary>
    public const string FormatName = "apk";

    /// <summary>A package is installed for the whole machine; the package manager elevates.</summary>
    public const string Scope = "machine";

    /// <summary>The package manager owns the files; the updater only reports updates.</summary>
    public const string Strategy = "notify";

    public const string Extension = ".apk";

    /// <summary>The signature type of the signature entry: RSA with SHA-256 (<c>abuild-sign -t RSA256</c>).</summary>
    public const string SignatureType = "RSA256";

    /// <summary>The release fragments' <c>minimumUpdater</c>, as for deb: 0.2.0 is the first Linux updater and knows <c>notify</c>.</summary>
    public const string MinimumUpdater = DebPackager.MinimumUpdater;

    /// <summary>The architectures an apk is built for.</summary>
    public static IReadOnlyList<Architecture> Architectures { get; } = [Architecture.X64, Architecture.Arm64];

    /// <summary>
    /// What the updater itself needs, added to the dependencies with <c>[updates]</c>: the musl C
    /// library it is linked against (its only <c>NEEDED</c> library), OpenSSL 3 by soname (.NET loads
    /// it for HTTPS and the feed's ECDSA signature) and the CA bundle HTTPS feeds are verified with.
    /// apk has no weak dependencies, so the CA bundle is a dependency; <c>ca-certificates-bundle</c>
    /// is the file alone, part of every Alpine base image.
    /// </summary>
    public static IReadOnlyList<string> UpdaterDepends(Architecture architecture) =>
        [$"so:libc.musl-{ApkArchitecture(architecture)}.so.1", "so:libcrypto.so.3", "so:libssl.so.3", "ca-certificates-bundle"];

    /// <summary>apk's name of an architecture: <c>x86_64</c> or <c>aarch64</c>.</summary>
    public static string ApkArchitecture(Architecture architecture)
    {
        if (architecture == Architecture.X64)
        {
            return "x86_64";
        }

        return architecture == Architecture.Arm64
            ? "aarch64"
            : throw new LinuxPackException($"apk packages support x64 and arm64, not {architecture.Name}.");
    }

    /// <summary>
    /// The package file name, <c>&lt;pkg&gt;-&lt;version&gt;-r0.&lt;arch&gt;.apk</c>, for example
    /// <c>notes-1.2.3_beta1-r0.x86_64.apk</c>: abuild's <c>&lt;pkg&gt;-&lt;version&gt;-r0.apk</c> plus
    /// the architecture, so that the x86_64 and aarch64 packages of one release can be assets of the
    /// same GitHub release (abuild keeps them apart in per-architecture directories instead).
    /// </summary>
    /// <exception cref="LinuxPackException">The version cannot be an apk version.</exception>
    public static string GetOutputFileName(ZexoroConfig config, Architecture architecture)
    {
        ArgumentNullException.ThrowIfNull(config);
        return $"{config.Linux.PackageName(config.App)}-{Version(config)}.{ApkArchitecture(architecture)}{Extension}";
    }

    /// <summary>Builds the package.</summary>
    /// <exception cref="LinuxPackException">An input is missing or invalid, or the package cannot be written.</exception>
    public static ApkPackResult Pack(ApkPackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = request.Config;
        var app = config.App;
        var input = config.Input;
        var architecture = request.Architecture;
        var apkArchitecture = ApkArchitecture(architecture);
        var version = Version(config);
        var package = config.Linux.PackageName(app);
        var maintainer = config.Linux.MaintainerOf(app);
        if (maintainer.Any(char.IsControl))
        {
            throw new LinuxPackException($"{(config.Linux.Maintainer is null ? "app.publisher" : "linux.maintainer")}: the apk maintainer must be one line.");
        }

        if (!Directory.Exists(input.Directory.FullPath))
        {
            throw new LinuxPackException($"input.dir: directory {input.Directory.FullPath} not found; build your application first.");
        }

        if (!File.Exists(input.Main.FullPath))
        {
            throw new LinuxPackException($"input.main: {input.Main.FullPath} not found.");
        }

        var icon = LinuxTarPackager.LoadIcon(config);
        var (updater, updates) = LinuxTarPackager.ReadUpdater(config, architecture, request.ResolveUpdater, request.Log, musl: true);
        WarnAboutGlibc(input.Main.FullPath, request.Log);
        if (config.Linux.Apk.License is null)
        {
            request.Log?.WriteLine("notice: [linux.apk] license is not set; the apk has no license field.");
        }

        if (request.Signer is null)
        {
            request.Log?.WriteLine(
                "notice: [linux.apk.signing] is not configured; the apk is unsigned. apk installs it only with --allow-untrusted.");
        }

        var depends = new List<string>();
        if (updater is not null)
        {
            depends.AddRange(UpdaterDepends(architecture));
        }

        depends.AddRange(config.Linux.Apk.Depends);

        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        var root = "opt/" + package;
        try
        {
            var items = new List<TarItem> { TarItem.Directory(root + "/app") };
            items.AddRange(LinuxPayload.Read(input.Directory.FullPath, root + "/app", input.Main.Value, outputDirectory));
            var record = InstallRecord.Create(
                app.Id, app.Version, new TargetPlatform(OperatingSystemFamily.Linux, architecture), FormatName, Scope, Strategy, updates, input.Main.Value);
            items.Add(TarItem.FromBytes(root + "/zexoro-install.json", record, TarItem.Regular));
            if (updater is not null)
            {
                items.Add(TarItem.FromBytes(root + "/zexoro-updater", updater, TarItem.Executable));
            }

            items.Add(TarItem.Link("usr/bin/" + package, "/" + root + "/app/" + input.Main.Value));
            var desktop = DesktopEntry.Create(app.Name, app.Description, ["/usr/bin/" + package], app.Id, config.Linux.Categories);
            items.Add(TarItem.FromBytes($"usr/share/applications/{app.Id}.desktop", Encoding.UTF8.GetBytes(desktop), TarItem.Regular));
            items.Add(TarItem.FromBytes(DebPackager.IconPath(app.Id, icon), icon.Bytes, TarItem.Regular));

            // apk needs an entry for every directory a file is in ("no dirent in archive"), as abuild writes them.
            DebPackager.AddParentDirectories(items);

            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, GetOutputFileName(config, architecture));
            var temporary = path + ".tmp";
            var dataPath = path + ".data.tmp";
            request.Log?.WriteLine($"Writing {Path.GetFileName(path)}");
            try
            {
                ApkArchive.WriteDataSegment(items, dataPath);
                string dataHash;
                using (var data = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    dataHash = Convert.ToHexStringLower(SHA256.HashData(data));
                }

                var info = PackageInfo(new PackageInfoInput(
                    package, version, apkArchitecture, maintainer, config.Linux.Apk.License, InstalledSize(items), depends, dataHash, app));
                var control = ApkArchive.CutSegment([(".PKGINFO", Encoding.UTF8.GetBytes(info))]);
                byte[]? signature = null;
                if (request.Signer is { } signer)
                {
                    request.Log?.WriteLine($"Signing the apk with {signer.Description} ({signer.KeyName})");
                    signature = ApkArchive.CutSegment([(SignatureEntryName(signer.KeyName), signer.Sign(control))]);
                }

                using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    if (signature is not null)
                    {
                        output.Write(signature);
                    }

                    output.Write(control);
                    using var data = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    data.CopyTo(output);
                }

                File.Move(temporary, path, overwrite: true);
                return new ApkPackResult(path, architecture, info);
            }
            finally
            {
                File.Delete(temporary);
                File.Delete(dataPath);
            }
        }
        catch (PayloadException ex)
        {
            throw new LinuxPackException(ex.Message, ex);
        }
        catch (ArgumentException ex)
        {
            throw new LinuxPackException($"Cannot build the package: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LinuxPackException($"Cannot build the package: {ex.Message}", ex);
        }
    }

    /// <summary>The name of the signature entry: <c>.SIGN.RSA256.&lt;key_name&gt;</c>.</summary>
    public static string SignatureEntryName(string keyName) => $".SIGN.{SignatureType}.{keyName}";

    /// <summary>The apk version of <c>app.version</c> with the release (<see cref="ApkVersion"/>).</summary>
    /// <exception cref="LinuxPackException">It cannot be mapped.</exception>
    private static string Version(ZexoroConfig config)
    {
        try
        {
            return ApkVersion.WithRelease(config.App.Version);
        }
        catch (ArgumentException ex)
        {
            throw new LinuxPackException($"app.version: {ex.Message.Split(" (Parameter", 2)[0]}.", ex);
        }
    }

    /// <summary>
    /// <c>size</c>: the installed size in bytes, the sum of the regular files' sizes as abuild
    /// computes it; at least 1, because apk-tools 2 treats a package of size 0 as virtual.
    /// </summary>
    internal static long InstalledSize(IEnumerable<TarItem> items) =>
        Math.Max(1, items.Where(i => i.Kind == TarItemKind.File).Sum(i => i.SourcePath is { } path ? new FileInfo(path).Length : i.Content?.Length ?? 0));

    internal sealed record PackageInfoInput(
        string Package,
        string Version,
        string Architecture,
        string Maintainer,
        string? License,
        long Size,
        IReadOnlyList<string> Depends,
        string DataHash,
        AppConfig App);

    /// <summary>
    /// The <c>.PKGINFO</c> file, in abuild's field order: <c>key = value</c> lines. The build
    /// date is the fixed <see cref="ReproducibleTar.Timestamp"/>, as the rpm's BuildTime.
    /// </summary>
    internal static string PackageInfo(PackageInfoInput input)
    {
        var text = new StringBuilder();
        void Field(string name, string value) => text.Append(name).Append(" = ").Append(value).Append('\n');
        Field("pkgname", input.Package);
        Field("pkgver", input.Version);
        Field("pkgdesc", Summary(input.App));
        if (input.App.Url is { } url)
        {
            Field("url", url.AbsoluteUri);
        }

        Field("builddate", ReproducibleTar.Timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        Field("packager", input.Maintainer);
        Field("size", input.Size.ToString(CultureInfo.InvariantCulture));
        Field("arch", input.Architecture);
        Field("origin", input.Package);
        Field("maintainer", input.Maintainer);
        if (input.License is { } license)
        {
            Field("license", license);
        }

        foreach (var depend in input.Depends)
        {
            Field("depend", depend);
        }

        Field("datahash", input.DataHash);
        return text.ToString();
    }

    /// <summary>
    /// <c>pkgdesc</c>: the first non-empty line of <c>app.description</c>, or the application name;
    /// abuild refuses descriptions over 128 characters, so longer ones are cut there.
    /// </summary>
    internal static string Summary(AppConfig app)
    {
        var line = (app.Description ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(l => new string(l.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim())
            .FirstOrDefault(l => l.Length > 0) ?? app.Name.Trim();
        return line.Length <= 128 ? line : line[..127].TrimEnd() + "…";
    }

    /// <summary>Warns when the main executable needs glibc's dynamic loader, which Alpine does not have.</summary>
    private static void WarnAboutGlibc(string main, TextWriter? log)
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

        if (interpreter is not null && !interpreter.Contains("ld-musl", StringComparison.Ordinal))
        {
            log?.WriteLine(
                $"warning: input.main is linked against glibc ({interpreter}), but apk packages are for Alpine and other musl systems; "
                + "build it for linux-musl (for .NET: -r linux-musl-x64 or linux-musl-arm64).");
        }
    }
}
