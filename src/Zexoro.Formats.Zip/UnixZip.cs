using System.IO.Compression;

namespace Zexoro.Formats.Zip;

/// <summary>What a <see cref="UnixZipEntry"/> is.</summary>
public enum UnixZipEntryKind
{
    Directory,
    File,
    SymbolicLink,
}

/// <summary>One entry of a zip archive with Unix modes.</summary>
/// <param name="Name">The archive path, with '/' separators; directories without a trailing '/'.</param>
/// <param name="Kind">Directory, file or symbolic link.</param>
/// <param name="Mode">The permission bits (such as 0755); ignored for links, which get 0755.</param>
/// <param name="SourcePath">A file's content on disk; null for directories and links.</param>
/// <param name="LinkTarget">A link's target as stored; null otherwise.</param>
public sealed record UnixZipEntry(string Name, UnixZipEntryKind Kind, int Mode, string? SourcePath = null, string? LinkTarget = null);

/// <summary>
/// Writes zip archives that keep Unix modes and symbolic links, the layout <c>ditto -c -k</c>,
/// Info-ZIP's <c>zip -y</c> and macOS's Archive Utility use (docs/design/0006-desktop-formats.md
/// section 2, macOS zip): the central directory's "made by" host is 3 (Unix) and each entry's
/// external attributes hold <c>st_mode</c> (file type and permission bits) in their high 16 bits; a
/// symbolic link is an entry of type <c>S_IFLNK</c> whose stored content is the link target.
/// Extended attributes are not stored. Like <see cref="ReproducibleZip"/>, entries are sorted
/// ordinally and every timestamp is <see cref="ReproducibleZip.Timestamp"/>.
/// </summary>
public static class UnixZip
{
    /// <summary>The "version made by" host of Unix.</summary>
    internal const byte UnixHost = 3;

    internal const int TypeDirectory = 0x4000;
    internal const int TypeFile = 0x8000;
    internal const int TypeSymbolicLink = 0xA000;

    /// <summary>
    /// Reads every directory, file and symbolic link below <paramref name="source"/> (links are not
    /// followed), named <c>&lt;prefix&gt;/…</c>, with an entry for <paramref name="prefix"/> itself.
    /// Files with any execute bit get mode 0755, others 0644; directories 0755.
    /// </summary>
    /// <exception cref="IOException">The directory cannot be read.</exception>
    public static List<UnixZipEntry> ReadTree(string source, string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        var entries = new List<UnixZipEntry> { new(prefix, UnixZipEntryKind.Directory, 0x1ED) };
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };

        void Visit(string directory, string name)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", options))
            {
                var entryName = name + "/" + Path.GetFileName(path);
                var info = new FileInfo(path);
                if (info.LinkTarget is { } target)
                {
                    entries.Add(new UnixZipEntry(entryName, UnixZipEntryKind.SymbolicLink, 0x1ED, LinkTarget: target));
                }
                else if (Directory.Exists(path))
                {
                    entries.Add(new UnixZipEntry(entryName, UnixZipEntryKind.Directory, 0x1ED));
                    Visit(path, entryName);
                }
                else
                {
                    entries.Add(new UnixZipEntry(entryName, UnixZipEntryKind.File, IsExecutable(path) ? 0x1ED : 0x1A4, path));
                }
            }
        }

        Visit(Path.GetFullPath(source), prefix);
        return entries;
    }

    /// <summary>Writes <paramref name="entries"/> to <paramref name="destination"/>, sorted by name.</summary>
    /// <exception cref="IOException">A file cannot be read or the archive cannot be written.</exception>
    public static void Write(IEnumerable<UnixZipEntry> entries, string destination)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        var sorted = entries.Select(e => (Name: e.Kind == UnixZipEntryKind.Directory ? e.Name + "/" : e.Name, Entry: e)).ToList();
        sorted.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        using var stream = new FileStream(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, entry) in sorted)
            {
                switch (entry.Kind)
                {
                    case UnixZipEntryKind.Directory:
                    {
                        var directory = archive.CreateEntry(name, CompressionLevel.NoCompression);
                        directory.LastWriteTime = ReproducibleZip.Timestamp;
                        directory.ExternalAttributes = ((TypeDirectory | (entry.Mode & 0xFFF)) << 16) | ReproducibleZip.DirectoryAttribute;
                        break;
                    }

                    case UnixZipEntryKind.SymbolicLink:
                    {
                        var link = archive.CreateEntry(name, CompressionLevel.NoCompression);
                        link.LastWriteTime = ReproducibleZip.Timestamp;
                        link.ExternalAttributes = (TypeSymbolicLink | 0x1ED) << 16;
                        using var output = link.Open();
                        output.Write(System.Text.Encoding.UTF8.GetBytes(entry.LinkTarget ?? throw new ArgumentException($"The link {entry.Name} has no target.", nameof(entries))));
                        break;
                    }

                    default:
                    {
                        var file = archive.CreateEntry(name, CompressionLevel.Optimal);
                        file.LastWriteTime = ReproducibleZip.Timestamp;
                        file.ExternalAttributes = (TypeFile | (entry.Mode & 0xFFF)) << 16;
                        using var input = new FileStream(
                            entry.SourcePath ?? throw new ArgumentException($"The file {entry.Name} has no source.", nameof(entries)),
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read);
                        using var output = file.Open();
                        input.CopyTo(output);
                        break;
                    }
                }
            }
        }

        ReproducibleZip.MarkMadeBy(stream, UnixHost);
    }

    /// <summary>Writes the tree below <paramref name="source"/> as <c>&lt;prefix&gt;/…</c> (see <see cref="ReadTree"/>).</summary>
    /// <exception cref="IOException">A file cannot be read or the archive cannot be written.</exception>
    public static void WriteDirectory(string source, string prefix, string destination) => Write(ReadTree(source, prefix), destination);

    private static bool IsExecutable(string path) =>
        !OperatingSystem.IsWindows()
        && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
}
