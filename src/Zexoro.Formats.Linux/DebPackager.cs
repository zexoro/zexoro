using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Zexoro.Configuration;
using Zexoro.Formats.Nsis;
using Zexoro.Platforms;

namespace Zexoro.Formats.Linux;

/// <summary>What <see cref="DebPackager.Pack"/> needs.</summary>
public sealed record DebPackRequest
{
    public required ZexoroConfig Config { get; init; }

    /// <summary>The target architecture (x64 or arm64).</summary>
    public required Architecture Architecture { get; init; }

    /// <summary>The directory the package is written to; created if missing.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Receives progress messages and notices; null for none.</summary>
    public TextWriter? Log { get; init; }

    /// <summary>
    /// Returns the zexoro-updater build for a runtime; needed with <c>[updates]</c>. It throws
    /// <see cref="LinuxPackException"/> when none is available.
    /// </summary>
    public Func<UpdaterRuntime, UnixUpdaterBinary>? ResolveUpdater { get; init; }
}

/// <summary>The result of <see cref="DebPackager.Pack"/>.</summary>
/// <param name="PackagePath">The package that was written.</param>
/// <param name="Architecture">Its architecture.</param>
/// <param name="Control">The control file, as written.</param>
public sealed record DebPackResult(string PackagePath, Architecture Architecture, string Control);

/// <summary>
/// Debian binary packages (docs/design/0006-desktop-formats.md sections 2 and 3, "As implemented
/// (step 6)"): an ar archive of <c>debian-binary</c>, <c>control.tar.gz</c> and
/// <c>data.tar.gz</c>, written in .NET and reproducible. The package installs
/// <c>/opt/&lt;pkg&gt;/app/</c>, <c>/opt/&lt;pkg&gt;/zexoro-install.json</c> (format <c>deb</c>,
/// scope <c>machine</c>, strategy <c>notify</c>), with <c>[updates]</c>
/// <c>/opt/&lt;pkg&gt;/zexoro-updater</c>, the link <c>/usr/bin/&lt;pkg&gt;</c> to the main
/// executable, a <c>.desktop</c> file and the icon. It has no maintainer scripts: dpkg's triggers
/// refresh the desktop and icon caches.
/// </summary>
public static class DebPackager
{
    /// <summary>The record's and the feed artifact's <c>format</c>.</summary>
    public const string FormatName = "deb";

    /// <summary>A package is installed for the whole machine; the package manager elevates.</summary>
    public const string Scope = "machine";

    /// <summary>The package manager owns the files; the updater only reports updates.</summary>
    public const string Strategy = "notify";

    public const string Extension = ".deb";

    /// <summary>
    /// The release fragments' <c>minimumUpdater</c>: 0.2.0 (any prerelease of it) is the first
    /// zexoro-updater built for Linux, and it knows the <c>notify</c> strategy; no released updater
    /// is older on Linux, so no higher minimum is needed.
    /// </summary>
    public const string MinimumUpdater = "0.2.0-0";

    /// <summary>
    /// What the updater itself needs, added to <c>Depends</c> with <c>[updates]</c>: the C library
    /// it was built against (glibc 2.28, docs/design/0006-desktop-formats.md step 4) and OpenSSL,
    /// which .NET loads for HTTPS and for verifying the feed's ECDSA signature (1.1 or 3; the
    /// package is called <c>libssl3t64</c> since Debian 13 and Ubuntu 24.04).
    /// </summary>
    public static IReadOnlyList<string> UpdaterDepends { get; } = ["libc6 (>= 2.28)", "libssl3t64 | libssl3 | libssl1.1"];

    /// <summary>Recommended with <c>[updates]</c>: the CA certificates HTTPS feeds are verified with.</summary>
    public const string UpdaterRecommends = "ca-certificates";

    /// <summary>The sizes the hicolor icon theme has directories for; other PNG icons go to <c>/usr/share/pixmaps</c>.</summary>
    public static IReadOnlyList<int> HicolorSizes { get; } = [16, 22, 24, 32, 36, 48, 64, 72, 96, 128, 192, 256, 512];

    /// <summary>The architectures a deb is built for.</summary>
    public static IReadOnlyList<Architecture> Architectures { get; } = [Architecture.X64, Architecture.Arm64];

    /// <summary>Debian's name of an architecture: <c>amd64</c> or <c>arm64</c>.</summary>
    public static string DebArchitecture(Architecture architecture)
    {
        if (architecture == Architecture.X64)
        {
            return "amd64";
        }

        return architecture == Architecture.Arm64
            ? "arm64"
            : throw new LinuxPackException($"deb packages support x64 and arm64, not {architecture.Name}.");
    }

    /// <summary>
    /// The package file name in Debian's convention, <c>&lt;pkg&gt;_&lt;version&gt;_&lt;arch&gt;.deb</c>,
    /// for example <c>notes_1.2.3~beta.1_amd64.deb</c>.
    /// </summary>
    /// <exception cref="LinuxPackException">The version cannot be a Debian version.</exception>
    public static string GetOutputFileName(ZexoroConfig config, Architecture architecture)
    {
        ArgumentNullException.ThrowIfNull(config);
        return $"{config.Linux.PackageName(config.App)}_{Version(config)}_{DebArchitecture(architecture)}{Extension}";
    }

    /// <summary>Builds the package.</summary>
    /// <exception cref="LinuxPackException">An input is missing or invalid, or the package cannot be written.</exception>
    public static DebPackResult Pack(DebPackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = request.Config;
        var app = config.App;
        var input = config.Input;
        var architecture = request.Architecture;
        var debArchitecture = DebArchitecture(architecture);
        var version = Version(config);
        var package = config.Linux.PackageName(app);
        var maintainer = config.Linux.MaintainerOf(app);
        var maintainerKey = config.Linux.Maintainer is null ? "app.publisher" : "linux.maintainer";
        if (maintainer.Any(char.IsControl))
        {
            throw new LinuxPackException($"{maintainerKey}: the deb Maintainer field must be one line.");
        }

        if (!Directory.Exists(input.Directory.FullPath))
        {
            throw new LinuxPackException($"input.dir: directory {input.Directory.FullPath} not found; build your application first.");
        }

        if (!File.Exists(input.Main.FullPath))
        {
            throw new LinuxPackException($"input.main: {input.Main.FullPath} not found.");
        }

        if (config.Linux.Maintainer is null && !maintainer.Contains('<', StringComparison.Ordinal))
        {
            request.Log?.WriteLine(
                $"notice: [linux] maintainer is not set; the deb Maintainer is app.publisher ('{maintainer}'). Debian expects 'Name <email>'.");
        }

        var icon = LinuxTarPackager.LoadIcon(config);
        var (updater, updates) = LinuxTarPackager.ReadUpdater(config, architecture, request.ResolveUpdater, request.Log);
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
            items.Add(TarItem.FromBytes(IconPath(app.Id, icon), icon.Bytes, TarItem.Regular));
            AddParentDirectories(items);

            var depends = new List<string>();
            string? recommends = null;
            if (updater is not null)
            {
                depends.AddRange(UpdaterDepends);
                recommends = UpdaterRecommends;
            }

            depends.AddRange(config.Linux.Deb.Depends);
            var control = Control(package, version, debArchitecture, maintainer, InstalledSize(items), depends, recommends, app);

            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, GetOutputFileName(config, architecture));
            var temporary = path + ".tmp";
            var controlTar = path + ".control.tmp";
            var dataTar = path + ".data.tmp";
            request.Log?.WriteLine($"Writing {Path.GetFileName(path)}");
            try
            {
                ReproducibleTar.WriteDebian(
                    [
                        TarItem.FromBytes("control", Encoding.UTF8.GetBytes(control), TarItem.Regular),
                        TarItem.FromBytes("md5sums", Md5Sums(items), TarItem.Regular),
                    ],
                    controlTar);
                ReproducibleTar.WriteDebian(items, dataTar);
                using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    ArArchive.WriteHeader(output);
                    ArArchive.WriteMember(output, "debian-binary", "2.0\n"u8.ToArray());
                    ArArchive.WriteMember(output, "control.tar.gz", controlTar);
                    ArArchive.WriteMember(output, "data.tar.gz", dataTar);
                }

                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
                File.Delete(controlTar);
                File.Delete(dataTar);
            }

            return new DebPackResult(path, architecture, control);
        }
        catch (PayloadException ex)
        {
            throw new LinuxPackException(ex.Message, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LinuxPackException($"Cannot build the package: {ex.Message}", ex);
        }
    }

    /// <summary>The Debian version of <c>app.version</c> (<see cref="DebVersion"/>).</summary>
    /// <exception cref="LinuxPackException">It cannot be mapped.</exception>
    private static string Version(ZexoroConfig config)
    {
        try
        {
            return DebVersion.From(config.App.Version);
        }
        catch (ArgumentException ex)
        {
            throw new LinuxPackException($"app.version: {ex.Message.Split(" (Parameter", 2)[0]}.", ex);
        }
    }

    /// <summary>
    /// The icon's path: <c>usr/share/icons/hicolor/&lt;n&gt;x&lt;n&gt;/apps/&lt;app.id&gt;.png</c> when
    /// the theme has a directory for its size, otherwise <c>usr/share/pixmaps/&lt;app.id&gt;.png</c>,
    /// which icon lookups search as well.
    /// </summary>
    internal static string IconPath(string appId, PngImage icon) =>
        icon.Width == icon.Height && HicolorSizes.Contains(icon.Width)
            ? $"usr/share/icons/hicolor/{icon.Width}x{icon.Width}/apps/{appId}.png"
            : $"usr/share/pixmaps/{appId}.png";

    /// <summary>Adds an entry for every parent directory that has none, as dpkg-deb does.</summary>
    private static void AddParentDirectories(List<TarItem> items)
    {
        var present = items.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in items.Select(i => i.Name).ToList())
        {
            for (var slash = name.IndexOf('/', StringComparison.Ordinal); slash > 0; slash = name.IndexOf('/', slash + 1))
            {
                var parent = name[..slash];
                if (present.Add(parent))
                {
                    items.Add(TarItem.Directory(parent));
                }
            }
        }
    }

    /// <summary>
    /// <c>Installed-Size</c> in KiB, estimated as dpkg-gencontrol does: each file rounded up to
    /// whole KiB, one KiB for each directory and link.
    /// </summary>
    internal static long InstalledSize(IEnumerable<TarItem> items)
    {
        long total = 0;
        foreach (var item in items)
        {
            total += item.Kind == TarItemKind.File ? (Length(item) + 1023) / 1024 : 1;
        }

        return total;
    }

    private static long Length(TarItem item) => item.SourcePath is { } path ? new FileInfo(path).Length : item.Content?.Length ?? 0;

    /// <summary>The <c>md5sums</c> control file: <c>&lt;md5&gt;  &lt;path&gt;</c> per regular file, sorted by path.</summary>
    internal static byte[] Md5Sums(IEnumerable<TarItem> items)
    {
        var text = new StringBuilder();
        foreach (var item in items.Where(i => i.Kind == TarItemKind.File).OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            byte[] hash;
#pragma warning disable CA5351 // The md5sums format is defined with MD5; dpkg uses it to detect changed files, not for security.
            if (item.SourcePath is { } path)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                hash = MD5.HashData(stream);
            }
            else
            {
                hash = MD5.HashData(item.Content ?? []);
            }
#pragma warning restore CA5351

            text.Append(Convert.ToHexStringLower(hash)).Append("  ").Append(item.Name).Append('\n');
        }

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    /// <summary>The control file (deb-control(5)).</summary>
    internal static string Control(
        string package, string version, string architecture, string maintainer, long installedSize, IReadOnlyList<string> depends, string? recommends, AppConfig app)
    {
        var text = new StringBuilder();
        void Field(string name, string value) => text.Append(name).Append(": ").Append(value).Append('\n');
        Field("Package", package);
        Field("Version", version);
        Field("Architecture", architecture);
        Field("Maintainer", maintainer);
        Field("Installed-Size", installedSize.ToString(CultureInfo.InvariantCulture));
        if (depends.Count > 0)
        {
            Field("Depends", string.Join(", ", depends));
        }

        if (recommends is not null)
        {
            Field("Recommends", recommends);
        }

        Field("Section", "misc");
        Field("Priority", "optional");
        if (app.Url is { } url)
        {
            Field("Homepage", url.AbsoluteUri);
        }

        text.Append(Description(app));
        return text.ToString();
    }

    /// <summary>
    /// The <c>Description</c> field: the first line of <c>app.description</c> (or the application
    /// name) as the synopsis; further lines of the description become the extended description,
    /// each indented by one space, an empty line written as <c> .</c>.
    /// </summary>
    internal static string Description(AppConfig app)
    {
        var lines = (app.Description is { } description && !string.IsNullOrWhiteSpace(description) ? description : app.Name)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(l => new string(l.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).TrimEnd())
            .ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var text = new StringBuilder("Description: ").Append(lines[0].Trim()).Append('\n');
        foreach (var line in lines.Skip(1))
        {
            text.Append(' ').Append(line.Length == 0 ? "." : line).Append('\n');
        }

        return text.ToString();
    }
}

/// <summary>
/// The ar archive format as dpkg reads it (deb(5)): the global header, then for each member a
/// 60-byte header and the data, padded to an even length with '\n'. Every member gets the same
/// timestamp as the tar entries, owner and group 0 and mode 100644, so the bytes are reproducible.
/// </summary>
internal static class ArArchive
{
    /// <summary>The modification time of every member: <see cref="ReproducibleTar.Timestamp"/>.</summary>
    private static readonly long Timestamp = ReproducibleTar.Timestamp.ToUnixTimeSeconds();

    public static void WriteHeader(Stream output) => output.Write("!<arch>\n"u8);

    public static void WriteMember(Stream output, string name, byte[] content)
    {
        WriteMemberHeader(output, name, content.Length);
        output.Write(content);
        Pad(output, content.Length);
    }

    public static void WriteMember(Stream output, string name, string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = source.Length;
        WriteMemberHeader(output, name, length);
        source.CopyTo(output);
        Pad(output, length);
    }

    private static void WriteMemberHeader(Stream output, string name, long size)
    {
        if (name.Length > 16 || name.Any(c => c is < '!' or > '~' || c == '/'))
        {
            throw new ArgumentException($"'{name}' is not a valid ar member name.", nameof(name));
        }

        var header = string.Create(
            CultureInfo.InvariantCulture,
            $"{name,-16}{Timestamp,-12}{0,-6}{0,-6}{"100644",-8}{size,-10}`\n");
        output.Write(Encoding.ASCII.GetBytes(header));
    }

    private static void Pad(Stream output, long length)
    {
        if (length % 2 != 0)
        {
            output.WriteByte((byte)'\n');
        }
    }
}
