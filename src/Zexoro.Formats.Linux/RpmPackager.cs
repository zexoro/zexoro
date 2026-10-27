using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Zexoro.Configuration;
using Zexoro.Formats.Nsis;
using Zexoro.Platforms;

namespace Zexoro.Formats.Linux;

/// <summary>Signs rpm headers; made by the caller from <c>[linux.rpm.signing]</c>.</summary>
/// <param name="Description">The key, for the log (for example its fingerprint and user ID).</param>
/// <param name="SignHeader">Returns the OpenPGP signature packet over the main header's bytes.</param>
public sealed record RpmHeaderSigner(string Description, Func<byte[], byte[]> SignHeader);

/// <summary>What <see cref="RpmPackager.Pack"/> needs.</summary>
public sealed record RpmPackRequest
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

    /// <summary>Signs the header, or null for an unsigned package.</summary>
    public RpmHeaderSigner? Signer { get; init; }
}

/// <summary>The result of <see cref="RpmPackager.Pack"/>.</summary>
/// <param name="PackagePath">The package that was written.</param>
/// <param name="Architecture">Its architecture.</param>
/// <param name="Requires">The header's requirements as text (<c>name op version</c>), rpmlib ones included.</param>
public sealed record RpmPackResult(string PackagePath, Architecture Architecture, IReadOnlyList<string> Requires);

/// <summary>
/// rpm packages (docs/design/0006-desktop-formats.md sections 2 and 3, "As implemented (step
/// 7)"): the RPM v4 file format written in .NET — a 96-byte lead, the signature header, the main
/// header and a gzip-compressed cpio (newc) payload — reproducible like the deb. The layout is the
/// deb's: <c>/opt/&lt;pkg&gt;/app/</c>, <c>/opt/&lt;pkg&gt;/zexoro-install.json</c> (format
/// <c>rpm</c>, scope <c>machine</c>, strategy <c>notify</c>), with <c>[updates]</c>
/// <c>/opt/&lt;pkg&gt;/zexoro-updater</c>, the link <c>/usr/bin/&lt;pkg&gt;</c>, a
/// <c>.desktop</c> file and the icon. The package owns only <c>/opt/&lt;pkg&gt;</c> and what lies
/// below it, plus its own files elsewhere: system directories belong to the distribution's
/// <c>filesystem</c> package, whose modes differ between distributions. No scriptlets.
/// </summary>
public static class RpmPackager
{
    /// <summary>The record's and the feed artifact's <c>format</c>.</summary>
    public const string FormatName = "rpm";

    /// <summary>A package is installed for the whole machine; the package manager elevates.</summary>
    public const string Scope = "machine";

    /// <summary>The package manager owns the files; the updater only reports updates.</summary>
    public const string Strategy = "notify";

    public const string Extension = ".rpm";

    /// <summary>The release fragments' <c>minimumUpdater</c>, as for deb: 0.2.0 is the first Linux updater and knows <c>notify</c>.</summary>
    public const string MinimumUpdater = DebPackager.MinimumUpdater;

    /// <summary>
    /// What the updater itself needs, added to <c>Requires</c> with <c>[updates]</c> (the deb's
    /// <c>Depends</c> in rpm's terms): glibc 2.28, as the version node the C library provides on
    /// every rpm distribution, and OpenSSL 3 or 1.1 by soname (Fedora's <c>openssl-libs</c>,
    /// openSUSE's <c>libopenssl3</c> or <c>libopenssl1_1</c>).
    /// </summary>
    public static IReadOnlyList<string> UpdaterRequires { get; } = ["libc.so.6(GLIBC_2.28)(64bit)", "(libssl.so.3()(64bit) or libssl.so.1.1()(64bit))"];

    /// <summary>Recommended with <c>[updates]</c>: the CA certificates HTTPS feeds are verified with.</summary>
    public const string UpdaterRecommends = "ca-certificates";

    /// <summary>The architectures an rpm is built for.</summary>
    public static IReadOnlyList<Architecture> Architectures { get; } = [Architecture.X64, Architecture.Arm64];

    private const int ModeDirectory = 0x4000;
    private const int ModeRegular = 0x8000;
    private const int ModeLink = 0xA000;
    private const int Sha256Algorithm = 8;

    /// <summary>rpm's name of an architecture: <c>x86_64</c> or <c>aarch64</c>.</summary>
    public static string RpmArchitecture(Architecture architecture)
    {
        if (architecture == Architecture.X64)
        {
            return "x86_64";
        }

        return architecture == Architecture.Arm64
            ? "aarch64"
            : throw new LinuxPackException($"rpm packages support x64 and arm64, not {architecture.Name}.");
    }

    /// <summary>The architecture-qualified name rpmbuild provides, for example <c>notes(x86-64)</c>.</summary>
    private static string IsaName(string package, Architecture architecture) =>
        package + (architecture == Architecture.X64 ? "(x86-64)" : "(aarch-64)");

    /// <summary>
    /// The package file name in rpm's convention, <c>&lt;name&gt;-&lt;version&gt;-&lt;release&gt;.&lt;arch&gt;.rpm</c>,
    /// with <c>~</c> written as <c>.</c> (<see cref="PackageFileVersion"/>), for example
    /// <c>notes-1.2.3.beta.1-1.x86_64.rpm</c> for version <c>1.2.3~beta.1</c>.
    /// </summary>
    /// <exception cref="LinuxPackException">The version cannot be an rpm version.</exception>
    public static string GetOutputFileName(ZexoroConfig config, Architecture architecture)
    {
        ArgumentNullException.ThrowIfNull(config);
        return $"{config.Linux.PackageName(config.App)}-{PackageFileVersion.From(Version(config))}-{RpmVersion.Release}.{RpmArchitecture(architecture)}{Extension}";
    }

    /// <summary>Builds the package.</summary>
    /// <exception cref="LinuxPackException">An input is missing or invalid, or the package cannot be written.</exception>
    public static RpmPackResult Pack(RpmPackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = request.Config;
        var app = config.App;
        var input = config.Input;
        var architecture = request.Architecture;
        var rpmArchitecture = RpmArchitecture(architecture);
        var version = Version(config);
        var package = config.Linux.PackageName(app);
        var packager = config.Linux.MaintainerOf(app);
        if (packager.Any(char.IsControl))
        {
            throw new LinuxPackException($"{(config.Linux.Maintainer is null ? "app.publisher" : "linux.maintainer")}: the rpm Packager must be one line.");
        }

        if (!Directory.Exists(input.Directory.FullPath))
        {
            throw new LinuxPackException($"input.dir: directory {input.Directory.FullPath} not found; build your application first.");
        }

        if (!File.Exists(input.Main.FullPath))
        {
            throw new LinuxPackException($"input.main: {input.Main.FullPath} not found.");
        }

        var requires = new List<Dependency>();
        for (var i = 0; i < config.Linux.Rpm.Requires.Count; i++)
        {
            requires.Add(Dependency.Parse(config.Linux.Rpm.Requires[i], $"linux.rpm.requires[{i}]"));
        }

        var icon = LinuxTarPackager.LoadIcon(config);
        var (updater, updates) = LinuxTarPackager.ReadUpdater(config, architecture, request.ResolveUpdater, request.Log);
        if (updater is not null)
        {
            requires.InsertRange(0, UpdaterRequires.Select(r => Dependency.Parse(r, "updater")));
        }

        if (config.Linux.Rpm.License is null)
        {
            request.Log?.WriteLine("notice: [linux.rpm] license is not set; the rpm has no License tag.");
        }

        if (request.Signer is null)
        {
            request.Log?.WriteLine(
                "notice: [linux.rpm.signing] is not configured; the rpm is unsigned. dnf and zypper refuse unsigned packages from repositories, and RPM 6 can refuse them everywhere.");
        }

        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        var root = "opt/" + package;
        try
        {
            var items = new List<TarItem> { TarItem.Directory(root), TarItem.Directory(root + "/app") };
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

            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, GetOutputFileName(config, architecture));
            var temporary = path + ".tmp";
            var payloadPath = path + ".payload.tmp";
            request.Log?.WriteLine($"Writing {Path.GetFileName(path)}");
            try
            {
                var files = Sort(items);
                var payload = WritePayload(files, payloadPath);
                var fullRequires = RpmlibRequires(requires).Concat(requires).ToList();
                var header = MainHeader(new HeaderInput(
                    package, version, rpmArchitecture, IsaName(package, architecture), packager, config.Linux.Rpm.License, app, files, payload, fullRequires,
                    updater is null ? [] : [new Dependency(UpdaterRecommends, RpmSense.Any, string.Empty)]));
                if (request.Signer is { } signer)
                {
                    request.Log?.WriteLine($"Signing the rpm header with {signer.Description}");
                }

                var signature = SignatureHeader(header, payloadPath, payload.UncompressedSize, request.Signer);
                using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    output.Write(Lead($"{package}-{version}-{RpmVersion.Release}", architecture));
                    output.Write(signature);
                    output.Write(new byte[(8 - (signature.Length % 8)) % 8]);
                    output.Write(header);
                    using var source = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    source.CopyTo(output);
                }

                File.Move(temporary, path, overwrite: true);
                return new RpmPackResult(path, architecture, fullRequires.Select(d => d.ToString()).ToList());
            }
            finally
            {
                File.Delete(temporary);
                File.Delete(payloadPath);
            }
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

    /// <summary>The rpm Version of <c>app.version</c> (<see cref="RpmVersion"/>).</summary>
    /// <exception cref="LinuxPackException">It cannot be mapped.</exception>
    private static string Version(ZexoroConfig config)
    {
        try
        {
            return RpmVersion.From(config.App.Version);
        }
        catch (ArgumentException ex)
        {
            throw new LinuxPackException($"app.version: {ex.Message.Split(" (Parameter", 2)[0]}.", ex);
        }
    }

    /// <summary>One file of the package, with what the header records about it.</summary>
    internal sealed class RpmFile(TarItem item)
    {
        public TarItem Item { get; } = item;

        /// <summary>The absolute path, such as <c>/opt/notes/app</c>.</summary>
        public string Path { get; } = "/" + item.Name;

        public byte[] PathBytes { get; } = Encoding.UTF8.GetBytes("/" + item.Name);

        public int Mode => (int)Item.Mode | Item.Kind switch
        {
            TarItemKind.Directory => ModeDirectory,
            TarItemKind.SymbolicLink => ModeLink,
            _ => ModeRegular,
        };

        /// <summary>The size rpm records: the content's for files, the target's length for links, 4096 for directories.</summary>
        public long Size { get; set; }

        /// <summary>The hex SHA-256 of a file's content; empty for directories and links.</summary>
        public string Digest { get; set; } = string.Empty;
    }

    /// <summary>The payload as written: sizes and SHA-256 digests (hex) of the compressed and the uncompressed archive.</summary>
    internal sealed record PayloadInfo(long CompressedSize, long UncompressedSize, string CompressedDigest, string UncompressedDigest);

    private sealed record HeaderInput(
        string Name,
        string Version,
        string Architecture,
        string IsaName,
        string Packager,
        string? License,
        AppConfig App,
        IReadOnlyList<RpmFile> Files,
        PayloadInfo Payload,
        IReadOnlyList<Dependency> Requires,
        IReadOnlyList<Dependency> Recommends);

    /// <summary>
    /// Files sorted by path bytewise (rpm finds archive entries in the header's file list by binary
    /// search with strcmp); names are checked as <see cref="ReproducibleTar"/> checks them.
    /// </summary>
    internal static List<RpmFile> Sort(IEnumerable<TarItem> items)
    {
        var files = items.Select(i => new RpmFile(i)).ToList();
        files.Sort((a, b) => a.PathBytes.AsSpan().SequenceCompareTo(b.PathBytes));
        for (var i = 0; i < files.Count; i++)
        {
            var name = files[i].Item.Name;
            if (name.Length == 0 || name.StartsWith('/') || name.EndsWith('/') || name.Contains('\\', StringComparison.Ordinal)
                || name.Split('/').Any(s => s.Length == 0 || s is "." or ".."))
            {
                throw new ArgumentException($"'{name}' is not a valid package path.", nameof(items));
            }

            if (i > 0 && files[i - 1].Path == files[i].Path)
            {
                throw new ArgumentException($"'{name}' occurs twice.", nameof(items));
            }
        }

        return files;
    }

    /// <summary>
    /// Writes the payload: a cpio archive in the "new ASCII" (newc) format with <c>./</c>-prefixed
    /// names, compressed with gzip (the header's OS byte fixed to Unix, as in
    /// <see cref="ReproducibleTar"/>). Records each file's size and digest.
    /// </summary>
    internal static PayloadInfo WritePayload(IReadOnlyList<RpmFile> files, string destination)
    {
        using var uncompressedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long uncompressed = 0;
        using (var file = new FileStream(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true))
            {
                void Emit(ReadOnlySpan<byte> bytes)
                {
                    uncompressedHash.AppendData(bytes);
                    gzip.Write(bytes);
                    uncompressed += bytes.Length;
                }

                void Pad()
                {
                    var padding = (int)((4 - (uncompressed % 4)) % 4);
                    Emit(new byte[padding]);
                }

                void EntryHeader(string name, int mode, long size, int inode)
                {
                    if (size > uint.MaxValue)
                    {
                        throw new LinuxPackException($"{name} is larger than 4 GiB, which rpm's cpio payload cannot hold.");
                    }

                    var nameBytes = Encoding.UTF8.GetBytes(name);
                    var text = string.Create(
                        CultureInfo.InvariantCulture,
                        $"070701{inode:X8}{mode:X8}{0:X8}{0:X8}{1:X8}{ReproducibleTar.Timestamp.ToUnixTimeSeconds():X8}{size:X8}{0:X8}{0:X8}{0:X8}{0:X8}{nameBytes.Length + 1:X8}{0:X8}");
                    Emit(Encoding.ASCII.GetBytes(text));
                    Emit(nameBytes);
                    Emit([0]);
                    Pad();
                }

                for (var i = 0; i < files.Count; i++)
                {
                    var entry = files[i];
                    var name = "./" + entry.Item.Name;
                    switch (entry.Item.Kind)
                    {
                        case TarItemKind.Directory:
                            entry.Size = 4096;
                            EntryHeader(name, entry.Mode, 0, i + 1);
                            break;
                        case TarItemKind.SymbolicLink:
                            var target = Encoding.UTF8.GetBytes(entry.Item.LinkTarget ?? throw new ArgumentException($"The link '{entry.Item.Name}' has no target.", nameof(files)));
                            entry.Size = target.Length;
                            EntryHeader(name, entry.Mode, target.Length, i + 1);
                            Emit(target);
                            Pad();
                            break;
                        default:
                            using (var content = entry.Item.SourcePath is { } source
                                ? new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read)
                                : (Stream)new MemoryStream(entry.Item.Content ?? [], writable: false))
                            {
                                entry.Size = content.Length;
                                EntryHeader(name, entry.Mode, content.Length, i + 1);
                                using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                                var buffer = new byte[81920];
                                int read;
                                long total = 0;
                                while ((read = content.Read(buffer)) > 0)
                                {
                                    fileHash.AppendData(buffer, 0, read);
                                    Emit(buffer.AsSpan(0, read));
                                    total += read;
                                }

                                if (total != entry.Size)
                                {
                                    throw new IOException($"{entry.Item.SourcePath} changed while it was packed.");
                                }

                                entry.Digest = Convert.ToHexStringLower(fileHash.GetHashAndReset());
                            }

                            Pad();
                            break;
                    }
                }

                // The trailer entry, as rpm writes it: nlink 1, everything else zero.
                var trailer = Encoding.ASCII.GetBytes("TRAILER!!!");
                Emit(Encoding.ASCII.GetBytes(string.Create(
                    CultureInfo.InvariantCulture,
                    $"070701{0:X8}{0:X8}{0:X8}{0:X8}{1:X8}{0:X8}{0:X8}{0:X8}{0:X8}{0:X8}{0:X8}{trailer.Length + 1:X8}{0:X8}")));
                Emit(trailer);
                Emit([0]);
                Pad();
            }

            file.Position = ReproducibleTar.GzipOsOffset;
            file.WriteByte(ReproducibleTar.GzipOsUnix);
            file.Position = 0;
            var compressedDigest = SHA256.HashData(file);
            return new PayloadInfo(file.Length, uncompressed, Convert.ToHexStringLower(compressedDigest), Convert.ToHexStringLower(uncompressedHash.GetHashAndReset()));
        }
    }

    /// <summary>The main header (rpm's tags as rpmbuild writes them for a binary package).</summary>
    private static byte[] MainHeader(HeaderInput input)
    {
        var files = input.Files;
        var app = input.App;
        var (summary, description) = Texts(app);
        var buildTime = ReproducibleTar.Timestamp.ToUnixTimeSeconds();
        var evr = $"{input.Version}-{RpmVersion.Release}";
        long size = files.Sum(f => f.Size);
        if (size > uint.MaxValue)
        {
            throw new LinuxPackException("The package's files add up to more than 4 GiB, which zexoro's rpm packages cannot hold.");
        }

        var dirNames = new List<string>();
        var dirIndexes = new List<long>();
        var baseNames = new List<string>();
        foreach (var file in files)
        {
            var slash = file.Path.LastIndexOf('/');
            var directory = file.Path[..(slash + 1)];
            var index = dirNames.IndexOf(directory);
            if (index < 0)
            {
                index = dirNames.Count;
                dirNames.Add(directory);
            }

            dirIndexes.Add(index);
            baseNames.Add(file.Path[(slash + 1)..]);
        }

        var provides = new List<Dependency>
        {
            new(input.Name, RpmSense.Equal, evr),
            new(input.IsaName, RpmSense.Equal, evr),
        };
        provides.Sort();
        var requires = input.Requires.Distinct().Order().ToList();

        var header = new RpmHeaderBuilder()
            .StringArray(RpmTag.HeaderI18nTable, ["C"])
            .String(RpmTag.Name, input.Name)
            .String(RpmTag.Version, input.Version)
            .String(RpmTag.Release, RpmVersion.Release)
            .I18nString(RpmTag.Summary, summary)
            .I18nString(RpmTag.Description, description)
            .Int32(RpmTag.BuildTime, buildTime)
            .String(RpmTag.BuildHost, "localhost")
            .Int32(RpmTag.Size, size)
            .String(RpmTag.Vendor, app.Publisher)
            .String(RpmTag.Packager, input.Packager)
            .I18nString(RpmTag.Group, "Unspecified")
            .String(RpmTag.Os, "linux")
            .String(RpmTag.Arch, input.Architecture)
            .Int32(RpmTag.FileSizes, files.Select(f => f.Size).ToList())
            .Int16(RpmTag.FileModes, files.Select(f => f.Mode).ToList())
            .Int16(RpmTag.FileRdevs, files.Select(_ => 0).ToList())
            .Int32(RpmTag.FileMtimes, files.Select(_ => buildTime).ToList())
            .StringArray(RpmTag.FileDigests, files.Select(f => f.Digest).ToList())
            .StringArray(RpmTag.FileLinkTos, files.Select(f => f.Item.LinkTarget ?? string.Empty).ToList())
            .Int32(RpmTag.FileFlags, files.Select(_ => 0L).ToList())
            .StringArray(RpmTag.FileUserName, files.Select(_ => "root").ToList())
            .StringArray(RpmTag.FileGroupName, files.Select(_ => "root").ToList())
            .String(RpmTag.SourceRpm, $"{input.Name}-{evr}.src.rpm")
            .Int32(RpmTag.FileVerifyFlags, files.Select(_ => (long)uint.MaxValue).ToList())
            .StringArray(RpmTag.ProvideName, provides.Select(d => d.Name).ToList())
            .Int32(RpmTag.ProvideFlags, provides.Select(d => (long)d.Flags).ToList())
            .StringArray(RpmTag.ProvideVersion, provides.Select(d => d.Version).ToList())
            .Int32(RpmTag.RequireFlags, requires.Select(d => (long)d.Flags).ToList())
            .StringArray(RpmTag.RequireName, requires.Select(d => d.Name).ToList())
            .StringArray(RpmTag.RequireVersion, requires.Select(d => d.Version).ToList())
            .Int32(RpmTag.FileDevices, files.Select(_ => 1L).ToList())
            .Int32(RpmTag.FileInodes, files.Select((_, i) => (long)i + 1).ToList())
            .StringArray(RpmTag.FileLangs, files.Select(_ => string.Empty).ToList())
            .Int32(RpmTag.DirIndexes, dirIndexes)
            .StringArray(RpmTag.BaseNames, baseNames)
            .StringArray(RpmTag.DirNames, dirNames)
            .String(RpmTag.PayloadFormat, "cpio")
            .String(RpmTag.PayloadCompressor, "gzip")
            .String(RpmTag.PayloadFlags, "6")
            .Int32(RpmTag.FileDigestAlgo, Sha256Algorithm)
            .String(RpmTag.Encoding, "utf-8")
            .StringArray(RpmTag.PayloadDigest, [input.Payload.CompressedDigest])
            .Int32(RpmTag.PayloadDigestAlgo, Sha256Algorithm)
            .StringArray(RpmTag.PayloadDigestAlt, [input.Payload.UncompressedDigest]);
        if (input.License is { } license)
        {
            header.String(RpmTag.License, license);
        }

        if (app.Url is { } url)
        {
            header.String(RpmTag.Url, url.AbsoluteUri);
        }

        if (input.Recommends.Count > 0)
        {
            var recommends = input.Recommends.Order().ToList();
            header
                .StringArray(RpmTag.RecommendName, recommends.Select(d => d.Name).ToList())
                .StringArray(RpmTag.RecommendVersion, recommends.Select(d => d.Version).ToList())
                .Int32(RpmTag.RecommendFlags, recommends.Select(d => (long)d.Flags).ToList());
        }

        return header.Build(RpmTag.HeaderImmutable);
    }

    /// <summary>
    /// The signature header: the sizes, the legacy MD5 over header and payload and SHA-1 over the
    /// header (which rpm 4 still verifies when present), the SHA-256 over the header and, when
    /// signed, the OpenPGP signature over the header (RSAHEADER). The header in turn holds the
    /// payload's SHA-256, so the header signature covers the payload too.
    /// </summary>
    private static byte[] SignatureHeader(byte[] header, string payloadPath, long uncompressedSize, RpmHeaderSigner? signer)
    {
        long payloadLength;
        byte[] md5;
        using (var payload = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            payloadLength = payload.Length;
#pragma warning disable CA5351 // rpm's legacy RPMSIGTAG_MD5 is defined with MD5; it is an integrity check next to SHA-256, not the security.
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
#pragma warning restore CA5351
            hash.AppendData(header);
            var buffer = new byte[81920];
            int read;
            while ((read = payload.Read(buffer)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }

            md5 = hash.GetHashAndReset();
        }

        if (header.Length + payloadLength > uint.MaxValue || uncompressedSize > uint.MaxValue)
        {
            throw new LinuxPackException("The package is larger than 4 GiB, which zexoro's rpm packages cannot hold.");
        }

#pragma warning disable CA5350 // rpm's legacy RPMSIGTAG_SHA1 is defined with SHA-1; it is an integrity check next to SHA-256.
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(header));
#pragma warning restore CA5350
        var signature = new RpmHeaderBuilder()
            .String(RpmTag.Sha1Header, sha1)
            .String(RpmTag.Sha256Header, Convert.ToHexStringLower(SHA256.HashData(header)))
            .Int32(RpmTag.SignatureSize, header.Length + payloadLength)
            .Binary(RpmTag.SignatureMd5, md5)
            .Int32(RpmTag.SignaturePayloadSize, uncompressedSize);
        if (signer is not null)
        {
            signature.Binary(RpmTag.RsaHeader, signer.SignHeader(header));
        }

        return signature.Build(RpmTag.HeaderSignatures);
    }

    /// <summary>
    /// The 96-byte lead: magic, format 3.0, binary type, the architecture number of rpm's
    /// <c>rpmrc</c> (x86_64 1, aarch64 19), the name-version-release, OS 1 (Linux) and signature
    /// type 5 (a header-style signature follows). rpm reads only the magic and the type.
    /// </summary>
    internal static byte[] Lead(string nameVersionRelease, Architecture architecture)
    {
        var lead = new byte[96];
        lead[0] = 0xED;
        lead[1] = 0xAB;
        lead[2] = 0xEE;
        lead[3] = 0xDB;
        lead[4] = 3;
        lead[5] = 0;
        BinaryPrimitives.WriteInt16BigEndian(lead.AsSpan(6), 0);
        BinaryPrimitives.WriteInt16BigEndian(lead.AsSpan(8), (short)(architecture == Architecture.X64 ? 1 : 19));
        var name = Encoding.UTF8.GetBytes(nameVersionRelease);
        name.AsSpan(0, Math.Min(name.Length, 65)).CopyTo(lead.AsSpan(10));
        BinaryPrimitives.WriteInt16BigEndian(lead.AsSpan(76), 1);
        BinaryPrimitives.WriteInt16BigEndian(lead.AsSpan(78), 5);
        return lead;
    }

    /// <summary>The Summary (the first line of <c>app.description</c>, or the name) and the Description (the whole text).</summary>
    internal static (string Summary, string Description) Texts(AppConfig app)
    {
        var text = (app.Description is { } d && !string.IsNullOrWhiteSpace(d) ? d : app.Name).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var summary = new string(text.Split('\n')[0].Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        var description = new string(text.Select(c => c != '\n' && char.IsControl(c) ? ' ' : c).ToArray());
        return (summary, description);
    }

    /// <summary>
    /// The <c>rpmlib(…)</c> features the package needs, as rpmbuild adds them: compressed file
    /// names, file digests other than MD5, <c>./</c>-prefixed payload names and, for rich
    /// dependencies, RichDependencies.
    /// </summary>
    private static IEnumerable<Dependency> RpmlibRequires(IReadOnlyList<Dependency> requires)
    {
        const RpmSense Flags = RpmSense.Rpmlib | RpmSense.Less | RpmSense.Equal;
        yield return new Dependency("rpmlib(CompressedFileNames)", Flags, "3.0.4-1");
        yield return new Dependency("rpmlib(FileDigests)", Flags, "4.6.0-1");
        yield return new Dependency("rpmlib(PayloadFilesHavePrefix)", Flags, "4.0-1");
        if (requires.Any(r => r.Name.StartsWith('(')))
        {
            yield return new Dependency("rpmlib(RichDependencies)", Flags, "4.12.0-1");
        }
    }

    /// <summary>A dependency: name, comparison and version (empty without a version), ordered as rpmbuild orders them.</summary>
    internal sealed record Dependency(string Name, RpmSense Flags, string Version) : IComparable<Dependency>
    {
        /// <summary>
        /// Parses one dependency as written in a spec file: <c>name</c>, <c>name op version</c>
        /// (op one of <c>&lt; &lt;= = &gt;= &gt;</c>) or a rich dependency in parentheses, kept as it is.
        /// </summary>
        /// <exception cref="LinuxPackException">The text is none of these.</exception>
        public static Dependency Parse(string text, string key)
        {
            var trimmed = text.Trim();
            if (trimmed.StartsWith('('))
            {
                return trimmed.EndsWith(')') && trimmed.Count(c => c == '(') == trimmed.Count(c => c == ')')
                    ? new Dependency(trimmed, RpmSense.Any, string.Empty)
                    : throw Invalid();
            }

            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1 && IsToken(parts[0]))
            {
                return new Dependency(parts[0], RpmSense.Any, string.Empty);
            }

            if (parts.Length == 3 && IsToken(parts[0]) && IsToken(parts[2]))
            {
                RpmSense? flags = parts[1] switch
                {
                    "<" => RpmSense.Less,
                    "<=" => RpmSense.Less | RpmSense.Equal,
                    "=" or "==" => RpmSense.Equal,
                    ">=" => RpmSense.Greater | RpmSense.Equal,
                    ">" => RpmSense.Greater,
                    _ => null,
                };
                if (flags is { } sense)
                {
                    return new Dependency(parts[0], sense, parts[2]);
                }
            }

            throw Invalid();

            LinuxPackException Invalid() => new(
                $"{key}: '{text}' is not a dependency rpm understands; write 'name', 'name >= version' (with spaces around the operator) "
                + "or a rich dependency in parentheses, such as '(libicu or libicu74)'.");
        }

        private static bool IsToken(string value) =>
            value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ',' or '<' or '>' or '=');

        public int CompareTo(Dependency? other)
        {
            if (other is null)
            {
                return 1;
            }

            var byName = string.CompareOrdinal(Name, other.Name);
            if (byName != 0)
            {
                return byName;
            }

            var byVersion = string.CompareOrdinal(Version, other.Version);
            return byVersion != 0 ? byVersion : ((int)Flags).CompareTo((int)other.Flags);
        }

        public override string ToString()
        {
            if (Version.Length == 0)
            {
                return Name;
            }

            var op = (Flags & (RpmSense.Less | RpmSense.Greater | RpmSense.Equal)) switch
            {
                RpmSense.Less => "<",
                RpmSense.Less | RpmSense.Equal => "<=",
                RpmSense.Equal => "=",
                RpmSense.Greater | RpmSense.Equal => ">=",
                _ => ">",
            };
            return $"{Name} {op} {Version}";
        }
    }
}
