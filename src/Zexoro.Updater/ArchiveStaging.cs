using System.Formats.Tar;
using System.IO.Compression;

namespace Zexoro.Updater;

/// <summary>The archive is not a valid update: an unsafe or unexpected entry, or not an archive of its format at all.</summary>
internal sealed class InvalidArchiveException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>What an archive entry is.</summary>
internal enum ArchiveItemKind
{
    Directory,
    File,
    SymbolicLink,
}

/// <summary>One entry of an update archive, as read before anything is written.</summary>
/// <param name="Index">The entry's position in the archive, to find its data again.</param>
/// <param name="Name">The entry name as stored, with forward slashes.</param>
/// <param name="Kind">Directory, file or symbolic link.</param>
/// <param name="LinkTarget">A symbolic link's target, as stored.</param>
/// <param name="Mode">The Unix permission bits (no set-id or sticky bits), when the format records them.</param>
internal sealed record ArchiveItem(int Index, string Name, ArchiveItemKind Kind, string? LinkTarget = null, UnixFileMode? Mode = null);

/// <summary>
/// Stages a portable archive (docs/design/0006-desktop-formats.md, "replace — Windows zip, tar.gz")
/// next to an install: the archive has one top-level folder; its <c>app/</c> becomes
/// <c>&lt;root&gt;/app.new/</c> and each of its files <c>F</c> becomes <c>&lt;root&gt;/F.new</c>,
/// ready for <see cref="FileSwap"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every entry is checked before anything is written. Entry names are checked as <c>ToolsetCache</c>
/// checks toolset archives: no absolute paths, drive letters, backslashes, empty, <c>.</c> or
/// <c>..</c> segments, and every resolved path must stay below the root. Other top-level folders,
/// top-level directories other than <c>app</c> and duplicates are refused.
/// </para>
/// <para>
/// zip (Windows): links are refused, as are encrypted entries; names are compared without case, as
/// Windows does. tar.gz (Linux): names are compared exactly; Unix permission bits are restored (a
/// directory always keeps the owner's read, write and search rights, so a later update can remove
/// it); symbolic links are allowed inside <c>app/</c> only, with a relative target that stays inside
/// <c>app/</c> without passing through another link, and no entry may lie below a link. Hard links,
/// devices and FIFOs are refused. Links are created after every file, so nothing is ever written
/// through one.
/// </para>
/// </remarks>
internal static class ArchiveStaging
{
    private const UnixFileMode OwnerDirectoryRights = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Extracts <paramref name="archive"/> (positioned anywhere; read from the start) of
    /// <paramref name="format"/> (record format <c>zip</c> or <c>tar-gz</c>) into <paramref name="root"/>.
    /// </summary>
    /// <exception cref="InvalidArchiveException">The archive is malformed or contains an unsafe or unexpected entry.</exception>
    /// <exception cref="IOException">A file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">A file cannot be written.</exception>
    public static void Extract(Stream archive, string root, string format)
    {
        ArgumentNullException.ThrowIfNull(archive);
        using ArchiveSource source = format == ApplyCommand.TarGzFormat ? new TarGzSource(archive) : new ZipSource(archive);
        var plan = Plan(source.ReadItems(), root, source);
        foreach (var directory in plan.Directories)
        {
            Directory.CreateDirectory(directory.Path);
        }

        var written = 0;
        source.ReadFiles((index, data) =>
        {
            if (!plan.Files.TryGetValue(index, out var file))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
            using (var output = new FileStream(file.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                data.CopyTo(output);
            }

            if (file.Mode is { } mode && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(file.Path, mode);
            }

            written++;
        });
        if (written != plan.Files.Count)
        {
            throw new InvalidArchiveException("the update archive changed while it was read");
        }

        foreach (var (path, target) in plan.Links)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.CreateSymbolicLink(path, target);
        }

        if (!OperatingSystem.IsWindows())
        {
            // Deepest first, so a directory without write rights for others does not stop the rest.
            foreach (var directory in plan.Directories.Where(d => d.Mode is not null).OrderByDescending(d => d.Path.Length))
            {
                File.SetUnixFileMode(directory.Path, directory.Mode!.Value | OwnerDirectoryRights);
            }
        }
    }

    /// <summary>Checks every entry before anything is written; returns what to create.</summary>
    private static StagingPlan Plan(IReadOnlyList<ArchiveItem> items, string root, ArchiveSource source)
    {
        var names = source.NameComparer;
        var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var app = Path.Combine(root, FileSwap.AppDirectoryName + FileSwap.NewSuffix);
        var plan = new StagingPlan();
        plan.Directories.Add((app, null));
        var seen = new HashSet<string>(names);
        var appItems = new List<(ArchiveItem Item, string[] Segments)>();
        var links = new HashSet<string>(names);
        string? top = null;
        var hasApp = false;
        foreach (var item in items)
        {
            var name = item.Name;
            var isDirectory = item.Kind == ArchiveItemKind.Directory;
            var segments = (isDirectory && name.EndsWith('/') ? name[..^1] : name).Split('/');
            if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\', StringComparison.Ordinal) || name.Contains(':', StringComparison.Ordinal)
                || segments.Any(s => s.Length == 0 || s is "." or ".."))
            {
                throw new InvalidArchiveException($"the update archive contains an unsafe path: '{name}'");
            }

            if (item.Kind == ArchiveItemKind.SymbolicLink && !source.AllowsLinks)
            {
                throw new InvalidArchiveException($"the update archive contains a link, which is not allowed: '{name}'");
            }

            if (top is null)
            {
                top = segments[0];
            }
            else if (!string.Equals(top, segments[0], StringComparison.Ordinal))
            {
                throw new InvalidArchiveException($"the update archive has more than one top-level folder ('{top}' and '{segments[0]}')");
            }

            if (segments.Length == 1)
            {
                if (!isDirectory)
                {
                    throw new InvalidArchiveException($"the update archive has a file outside its top-level folder: '{name}'");
                }

                continue;
            }

            var isApp = names.Equals(segments[1], FileSwap.AppDirectoryName);
            string path;
            if (isApp && (segments.Length > 2 || isDirectory))
            {
                hasApp = true;
                if (segments.Length == 2)
                {
                    continue;
                }

                path = Path.Combine([app, .. segments[2..]]);
                appItems.Add((item, segments[2..]));
                if (item.Kind == ArchiveItemKind.SymbolicLink)
                {
                    links.Add(string.Join('/', segments[2..]));
                }
            }
            else if (segments.Length == 2 && item.Kind == ArchiveItemKind.File && !isApp)
            {
                path = Path.Combine(root, segments[1] + FileSwap.NewSuffix);
            }
            else
            {
                throw new InvalidArchiveException($"the update archive has an unexpected item: '{name}'; only app/ and files may be in its top-level folder");
            }

            // Defence in depth: the checks above already keep every path inside the root.
            if (!Path.GetFullPath(path).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidArchiveException($"the update archive's entry '{name}' points outside the install");
            }

            if (!seen.Add(Path.GetFullPath(path)))
            {
                throw new InvalidArchiveException($"the update archive contains '{name}' twice");
            }

            switch (item.Kind)
            {
                case ArchiveItemKind.Directory:
                    plan.Directories.Add((path, item.Mode));
                    break;
                case ArchiveItemKind.File:
                    plan.Files[item.Index] = (path, item.Mode);
                    break;
                default:
                    plan.Links.Add((path, item.LinkTarget!));
                    break;
            }
        }

        if (!hasApp)
        {
            throw new InvalidArchiveException("the update archive has no app folder");
        }

        if (links.Count > 0)
        {
            foreach (var (item, segments) in appItems)
            {
                for (var length = 1; length < segments.Length; length++)
                {
                    if (links.Contains(string.Join('/', segments[..length])))
                    {
                        throw new InvalidArchiveException($"the update archive's entry '{item.Name}' lies below a link");
                    }
                }

                if (item.Kind == ArchiveItemKind.SymbolicLink)
                {
                    CheckLink(item, segments, links);
                }
            }
        }

        return plan;
    }

    /// <summary>
    /// A link's target must be relative and, resolved from the link's directory, stay inside
    /// <c>app/</c> without passing through another link (whose own target could lead elsewhere).
    /// </summary>
    private static void CheckLink(ArchiveItem item, string[] segments, HashSet<string> links)
    {
        var target = item.LinkTarget ?? string.Empty;
        InvalidArchiveException Outside() =>
            new($"the update archive's link '{item.Name}' points outside the app folder ('{target}')");
        if (target.Length == 0 || target.StartsWith('/') || target.Contains('\\', StringComparison.Ordinal) || target.Contains('\0', StringComparison.Ordinal))
        {
            throw Outside();
        }

        var current = new List<string>(segments[..^1]);
        var parts = target.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            switch (parts[i])
            {
                case "":
                    throw Outside();
                case ".":
                    continue;
                case "..":
                    if (current.Count == 0)
                    {
                        throw Outside();
                    }

                    current.RemoveAt(current.Count - 1);
                    continue;
                default:
                    current.Add(parts[i]);
                    if (i < parts.Length - 1 && links.Contains(string.Join('/', current)))
                    {
                        throw new InvalidArchiveException($"the update archive's link '{item.Name}' passes through another link ('{target}')");
                    }

                    continue;
            }
        }
    }

    private sealed class StagingPlan
    {
        public List<(string Path, UnixFileMode? Mode)> Directories { get; } = [];

        public Dictionary<int, (string Path, UnixFileMode? Mode)> Files { get; } = [];

        public List<(string Path, string Target)> Links { get; } = [];
    }

    /// <summary>Reads one archive format: the entries first, then the file data.</summary>
    private abstract class ArchiveSource : IDisposable
    {
        /// <summary>How entry names are compared for duplicates and the <c>app</c> folder.</summary>
        public abstract StringComparer NameComparer { get; }

        public abstract bool AllowsLinks { get; }

        /// <exception cref="InvalidArchiveException">The archive is malformed or has an unsupported entry.</exception>
        public abstract IReadOnlyList<ArchiveItem> ReadItems();

        /// <summary>Calls <paramref name="write"/> with the index and data of every file entry, in order.</summary>
        /// <exception cref="InvalidArchiveException">The archive is malformed.</exception>
        public abstract void ReadFiles(Action<int, Stream> write);

        public abstract void Dispose();
    }

    private sealed class ZipSource : ArchiveSource
    {
        private const int UnixFileTypeMask = 0xF000;
        private const int UnixSymbolicLink = 0xA000;

        private readonly ZipArchive _zip;

        public ZipSource(Stream archive)
        {
            archive.Position = 0;
            try
            {
                _zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidArchiveException($"the update is not a valid zip archive: {ex.Message}", ex);
            }
        }

        public override StringComparer NameComparer => StringComparer.OrdinalIgnoreCase;

        public override bool AllowsLinks => false;

        public override IReadOnlyList<ArchiveItem> ReadItems()
        {
            var items = new List<ArchiveItem>();
            var index = 0;
            foreach (var entry in _zip.Entries)
            {
                if (entry.IsEncrypted)
                {
                    throw new InvalidArchiveException($"the update archive contains an encrypted entry: '{entry.FullName}'");
                }

                // A Unix symbolic link: the high 16 bits of the external attributes hold a Unix mode.
                var kind = ((entry.ExternalAttributes >> 16) & UnixFileTypeMask) == UnixSymbolicLink ? ArchiveItemKind.SymbolicLink
                    : entry.FullName.EndsWith('/') ? ArchiveItemKind.Directory
                    : ArchiveItemKind.File;
                items.Add(new ArchiveItem(index++, entry.FullName, kind));
            }

            return items;
        }

        public override void ReadFiles(Action<int, Stream> write)
        {
            var index = 0;
            foreach (var entry in _zip.Entries)
            {
                var current = index++;
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                try
                {
                    using var input = entry.Open();
                    write(current, input);
                }
                catch (InvalidDataException ex)
                {
                    throw new InvalidArchiveException($"the update archive's entry '{entry.FullName}' cannot be read: {ex.Message}", ex);
                }
            }
        }

        public override void Dispose() => _zip.Dispose();
    }

    /// <summary>A gzip-compressed tar archive, read twice from the start (it can only be read forward).</summary>
    private sealed class TarGzSource(Stream archive) : ArchiveSource
    {
        private const UnixFileMode PermissionBits = (UnixFileMode)0x1FF;

        public override StringComparer NameComparer => StringComparer.Ordinal;

        public override bool AllowsLinks => true;

        public override IReadOnlyList<ArchiveItem> ReadItems()
        {
            var items = new List<ArchiveItem>();
            Read((index, entry) =>
            {
                var mode = entry.Mode & PermissionBits;
                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        items.Add(new ArchiveItem(index, entry.Name, ArchiveItemKind.Directory, Mode: mode));
                        break;
                    case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                        items.Add(new ArchiveItem(index, entry.Name, ArchiveItemKind.File, Mode: mode));
                        break;
                    case TarEntryType.SymbolicLink:
                        items.Add(new ArchiveItem(index, entry.Name, ArchiveItemKind.SymbolicLink, entry.LinkName));
                        break;
                    case TarEntryType.GlobalExtendedAttributes:
                        // PAX global attributes apply to later entries; none of them matter here.
                        break;
                    case TarEntryType.HardLink:
                        throw new InvalidArchiveException($"the update archive contains a hard link, which is not allowed: '{entry.Name}'");
                    default:
                        throw new InvalidArchiveException($"the update archive contains an entry of type {entry.EntryType}, which is not allowed: '{entry.Name}'");
                }
            });
            return items;
        }

        public override void ReadFiles(Action<int, Stream> write) => Read((index, entry) =>
        {
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile)
            {
                write(index, entry.DataStream ?? Stream.Null);
            }
        });

        public override void Dispose()
        {
            // The archive stream belongs to the caller.
        }

        private void Read(Action<int, TarEntry> visit)
        {
            archive.Position = 0;
            try
            {
                using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
                using var reader = new TarReader(gzip, leaveOpen: true);
                var index = 0;
                while (reader.GetNextEntry(copyData: false) is { } entry)
                {
                    visit(index++, entry);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException or ArgumentException)
            {
                throw new InvalidArchiveException($"the update is not a valid tar.gz archive: {ex.Message}", ex);
            }
        }
    }
}
