namespace Zexoro.Formats.Linux;

/// <summary>The input directory cannot be packed as it is; the message is for the user.</summary>
public sealed class PayloadException(string message) : Exception(message);

/// <summary>
/// Reads the application directory for Linux archives: every directory, file and symbolic link,
/// with the Unix modes the archive gets (docs/design/0006-desktop-formats.md section 2).
/// </summary>
/// <remarks>
/// <para>
/// Modes are normalized so the archive does not depend on the umask or the host: directories and
/// executables 0755, other files 0644. A file is executable when it is the main executable, starts
/// with an ELF header or <c>#!</c>, or (on Linux and macOS hosts) has any execute bit set. Windows
/// has no execute bits, so packing there relies on the first three rules, which cover native
/// programs and scripts; the output is the same on every host when the input's execute bits agree
/// with them, as in usual build outputs.
/// </para>
/// <para>
/// Symbolic links are kept as links when their target is relative and stays inside the directory
/// without passing through another link: the rules the updater applies to update archives, so an
/// archive that installs also updates. Other links are an error, never followed.
/// </para>
/// </remarks>
public static class LinuxPayload
{
    /// <summary>
    /// Returns the items of <paramref name="source"/> named below <paramref name="prefix"/> (for
    /// example <c>Notes/app</c>), without an item for the prefix itself, skipping
    /// <paramref name="exclude"/> (the output directory, when it lies inside the input).
    /// </summary>
    /// <param name="source">The input directory.</param>
    /// <param name="prefix">The archive path of the directory.</param>
    /// <param name="main">The main executable, relative to <paramref name="source"/> with forward slashes.</param>
    /// <param name="exclude">A directory to skip, or null.</param>
    /// <exception cref="PayloadException">A symbolic link points outside the directory or is absolute.</exception>
    /// <exception cref="IOException">The directory cannot be read.</exception>
    public static List<TarItem> Read(string source, string prefix, string main, string? exclude)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        ArgumentNullException.ThrowIfNull(main);
        var items = new List<TarItem>();
        var links = new List<(string Relative, string Target)>();
        var excludePrefix = exclude is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(exclude)) + Path.DirectorySeparatorChar;
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };

        void Visit(string directory, string relative)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", options).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                var itemRelative = relative.Length == 0 ? name : relative + "/" + name;
                var archiveName = prefix + "/" + itemRelative;
                if (new FileInfo(path).LinkTarget is { } target)
                {
                    var normalized = OperatingSystem.IsWindows() ? target.Replace('\\', '/') : target;
                    links.Add((itemRelative, normalized));
                    items.Add(TarItem.Link(archiveName, normalized));
                }
                else if (Directory.Exists(path))
                {
                    if (excludePrefix is not null && (Path.GetFullPath(path) + Path.DirectorySeparatorChar).StartsWith(excludePrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    items.Add(TarItem.Directory(archiveName));
                    Visit(path, itemRelative);
                }
                else
                {
                    var executable = itemRelative == main || HasExecuteBit(path) || StartsLikeProgram(path);
                    items.Add(TarItem.FromFile(archiveName, path, executable ? TarItem.Executable : TarItem.Regular));
                }
            }
        }

        Visit(Path.GetFullPath(source), string.Empty);
        var linkSet = links.Select(l => l.Relative).ToHashSet(StringComparer.Ordinal);
        foreach (var (relative, target) in links)
        {
            CheckLink(relative, target, linkSet);
        }

        return items;
    }

    /// <summary>Whether the file starts with an ELF header or <c>#!</c> (a script with an interpreter line).</summary>
    internal static bool StartsLikeProgram(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> start = stackalloc byte[4];
        var length = stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
        return (length == 4 && start.SequenceEqual("\u007FELF"u8)) || (length >= 2 && start[..2].SequenceEqual("#!"u8));
    }

    private static bool HasExecuteBit(string path) =>
        !OperatingSystem.IsWindows()
        && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    /// <summary>
    /// The link <paramref name="relative"/> must have a relative target that, resolved from the
    /// link's directory, stays inside the input without passing through another link.
    /// </summary>
    private static void CheckLink(string relative, string target, HashSet<string> links)
    {
        PayloadException Outside() => new(
            $"input.dir: the symbolic link '{relative}' points to '{target}'; Linux archives keep only links with a relative target inside input.dir, "
            + "which the updater can recreate. Replace it with a copy or a relative link.");
        if (target.Length == 0 || target.StartsWith('/') || target.Contains('\\', StringComparison.Ordinal) || target.Contains('\0', StringComparison.Ordinal))
        {
            throw Outside();
        }

        var current = relative.Split('/')[..^1].ToList();
        var parts = target.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            switch (parts[i])
            {
                case "":
                    // "a//b" or a trailing slash: the updater refuses these.
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
                        throw new PayloadException(
                            $"input.dir: the symbolic link '{relative}' points to '{target}', through the link '{string.Join('/', current)}'; "
                            + "Linux archives keep only links that do not pass through other links. Point it at the final target.");
                    }

                    continue;
            }
        }
    }
}
