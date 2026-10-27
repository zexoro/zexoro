using System.Formats.Tar;
using System.IO.Compression;

namespace Zexoro.Formats.Linux;

/// <summary>What a <see cref="TarItem"/> is.</summary>
public enum TarItemKind
{
    Directory,
    File,
    SymbolicLink,
}

/// <summary>One entry of a reproducible tar archive.</summary>
/// <param name="Name">The entry name with forward slashes, without a trailing slash.</param>
/// <param name="Kind">Directory, file or symbolic link.</param>
/// <param name="Mode">The permission bits (directories and files; links are always 0777).</param>
/// <param name="SourcePath">A file's content as a file on disk, or null.</param>
/// <param name="Content">A file's content in memory, when <paramref name="SourcePath"/> is null.</param>
/// <param name="LinkTarget">A symbolic link's target, as stored.</param>
public sealed record TarItem(string Name, TarItemKind Kind, UnixFileMode Mode, string? SourcePath = null, byte[]? Content = null, string? LinkTarget = null)
{
    /// <summary>rwxr-xr-x: directories and executables.</summary>
    public const UnixFileMode Executable = (UnixFileMode)0x1ED;

    /// <summary>rw-r--r--: other files.</summary>
    public const UnixFileMode Regular = (UnixFileMode)0x1A4;

    /// <summary>A directory with mode 0755.</summary>
    public static TarItem Directory(string name) => new(name, TarItemKind.Directory, Executable);

    /// <summary>A file read from <paramref name="path"/>.</summary>
    public static TarItem FromFile(string name, string path, UnixFileMode mode) => new(name, TarItemKind.File, mode, SourcePath: path);

    /// <summary>A file with the given bytes.</summary>
    public static TarItem FromBytes(string name, byte[] content, UnixFileMode mode) => new(name, TarItemKind.File, mode, Content: content);

    /// <summary>A symbolic link to <paramref name="target"/>.</summary>
    public static TarItem Link(string name, string target) => new(name, TarItemKind.SymbolicLink, (UnixFileMode)0x1FF, LinkTarget: target);
}

/// <summary>
/// Writes gzip-compressed tar archives whose bytes depend only on the entries' names, modes and
/// contents, not on the host, the time, the user or the file system: GNU tar format (long names as
/// <c>././@LongLink</c> entries; the POSIX pax format of <see cref="TarWriter"/> names its extended
/// headers after the process ID), entries sorted by name (ordinal), every timestamp
/// <see cref="Timestamp"/>, owner and group 0 without names, and the gzip header's operating system
/// byte fixed to Unix, since zlib records the host there.
/// </summary>
public static class ReproducibleTar
{
    /// <summary>The modification time of every entry: the same as the portable zip's (1980-02-01 00:00 UTC).</summary>
    public static DateTimeOffset Timestamp { get; } = new(1980, 2, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The gzip header's OS field (RFC 1952): 3, Unix.</summary>
    internal const byte GzipOsUnix = 3;

    /// <summary>Offset of the OS field in a gzip header.</summary>
    internal const int GzipOsOffset = 9;

    /// <summary>
    /// Writes <paramref name="items"/> to the gzip-compressed tar archive <paramref name="destination"/>,
    /// sorted by name. Directory names get a trailing slash.
    /// </summary>
    /// <exception cref="ArgumentException">A name is empty, absolute, contains '\', '.' or '..' segments, or occurs twice.</exception>
    /// <exception cref="IOException">A file cannot be read or the archive cannot be written.</exception>
    public static void Write(IEnumerable<TarItem> items, string destination)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        var sorted = items.ToList();
        sorted.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        for (var i = 0; i < sorted.Count; i++)
        {
            var name = sorted[i].Name;
            if (name.Length == 0 || name.StartsWith('/') || name.EndsWith('/') || name.Contains('\\', StringComparison.Ordinal)
                || name.Split('/').Any(s => s.Length == 0 || s is "." or ".."))
            {
                throw new ArgumentException($"'{name}' is not a valid archive entry name.", nameof(items));
            }

            if (i > 0 && sorted[i - 1].Name == name)
            {
                throw new ArgumentException($"'{name}' occurs twice.", nameof(items));
            }
        }

        using (var file = new FileStream(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true))
            using (var writer = new TarWriter(gzip, TarEntryFormat.Gnu, leaveOpen: true))
            {
                foreach (var item in sorted)
                {
                    WriteEntry(writer, item);
                }
            }

            file.Position = GzipOsOffset;
            file.WriteByte(GzipOsUnix);
        }
    }

    private static void WriteEntry(TarWriter writer, TarItem item)
    {
        var (type, name) = item.Kind switch
        {
            TarItemKind.Directory => (TarEntryType.Directory, item.Name + "/"),
            TarItemKind.SymbolicLink => (TarEntryType.SymbolicLink, item.Name),
            _ => (TarEntryType.RegularFile, item.Name),
        };
        var entry = new GnuTarEntry(type, name)
        {
            ModificationTime = Timestamp,
            Mode = item.Mode,
            Uid = 0,
            Gid = 0,
            UserName = string.Empty,
            GroupName = string.Empty,
        };
        switch (item.Kind)
        {
            case TarItemKind.SymbolicLink:
                entry.LinkName = item.LinkTarget ?? throw new ArgumentException($"The link '{item.Name}' has no target.", nameof(item));
                writer.WriteEntry(entry);
                break;
            case TarItemKind.File when item.SourcePath is { } path:
                using (var data = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    entry.DataStream = data;
                    writer.WriteEntry(entry);
                }

                break;
            case TarItemKind.File:
                using (var data = new MemoryStream(item.Content ?? [], writable: false))
                {
                    entry.DataStream = data;
                    writer.WriteEntry(entry);
                }

                break;
            default:
                writer.WriteEntry(entry);
                break;
        }
    }
}
