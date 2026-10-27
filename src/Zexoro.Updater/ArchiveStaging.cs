using System.IO.Compression;

namespace Zexoro.Updater;

/// <summary>The archive is not a valid update: an unsafe or unexpected entry, or not a zip at all.</summary>
internal sealed class InvalidArchiveException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// Stages a portable zip (docs/design/0006-desktop-formats.md, "replace — Windows zip") next to an
/// install: the archive has one top-level folder; its <c>app\</c> becomes <c>&lt;root&gt;\app.new\</c>
/// and each of its files <c>F</c> becomes <c>&lt;root&gt;\F.new</c>, ready for <see cref="FileSwap"/>.
/// </summary>
/// <remarks>
/// Entry names are checked as <c>ToolsetCache</c> checks toolset archives: no absolute paths, drive
/// letters, backslashes, empty, <c>.</c> or <c>..</c> segments, and every resolved path must stay
/// below the root. Links (Unix symlink entries) are refused, as are other top-level folders,
/// top-level directories other than <c>app</c>, encrypted entries and duplicates (compared without
/// case, as Windows does).
/// </remarks>
internal static class ArchiveStaging
{
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymbolicLink = 0xA000;

    /// <summary>Extracts <paramref name="archive"/> (positioned anywhere; read from the start) into <paramref name="root"/>.</summary>
    /// <exception cref="InvalidArchiveException">The archive is malformed or contains an unsafe or unexpected entry.</exception>
    /// <exception cref="IOException">A file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">A file cannot be written.</exception>
    public static void Extract(Stream archive, string root)
    {
        ArgumentNullException.ThrowIfNull(archive);
        archive.Position = 0;
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidArchiveException($"the update is not a valid zip archive: {ex.Message}", ex);
        }

        using (zip)
        {
            var plan = Plan(zip, root);
            foreach (var (entry, path) in plan)
            {
                if (entry is null)
                {
                    Directory.CreateDirectory(path);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                try
                {
                    using var input = entry.Open();
                    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    input.CopyTo(output);
                }
                catch (InvalidDataException ex)
                {
                    throw new InvalidArchiveException($"the update archive's entry '{entry.FullName}' cannot be read: {ex.Message}", ex);
                }
            }
        }
    }

    /// <summary>Checks every entry before anything is written; returns what to create (a null entry is a directory).</summary>
    private static List<(ZipArchiveEntry? Entry, string Path)> Plan(ZipArchive zip, string root)
    {
        var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var app = Path.Combine(root, FileSwap.AppDirectoryName + FileSwap.NewSuffix);
        var plan = new List<(ZipArchiveEntry?, string)> { (null, app) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? top = null;
        var hasApp = false;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            var isDirectory = name.EndsWith('/');
            var segments = (isDirectory ? name[..^1] : name).Split('/');
            if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\', StringComparison.Ordinal) || name.Contains(':', StringComparison.Ordinal)
                || segments.Any(s => s.Length == 0 || s is "." or ".."))
            {
                throw new InvalidArchiveException($"the update archive contains an unsafe path: '{name}'");
            }

            if (IsLink(entry))
            {
                throw new InvalidArchiveException($"the update archive contains a link, which is not allowed: '{name}'");
            }

            if (entry.IsEncrypted)
            {
                throw new InvalidArchiveException($"the update archive contains an encrypted entry: '{name}'");
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

            string path;
            if (string.Equals(segments[1], FileSwap.AppDirectoryName, StringComparison.OrdinalIgnoreCase) && (segments.Length > 2 || isDirectory))
            {
                hasApp = true;
                if (segments.Length == 2)
                {
                    continue;
                }

                path = Path.Combine([app, .. segments[2..]]);
            }
            else if (segments.Length == 2 && !isDirectory && !string.Equals(segments[1], FileSwap.AppDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                path = Path.Combine(root, segments[1] + FileSwap.NewSuffix);
            }
            else
            {
                throw new InvalidArchiveException($"the update archive has an unexpected item: '{name}'; only app\\ and files may be in its top-level folder");
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

            plan.Add((isDirectory ? null : entry, path));
        }

        if (!hasApp)
        {
            throw new InvalidArchiveException("the update archive has no app folder");
        }

        return plan;
    }

    /// <summary>A Unix symbolic link: the high 16 bits of the external attributes hold a Unix mode.</summary>
    private static bool IsLink(ZipArchiveEntry entry) => ((entry.ExternalAttributes >> 16) & UnixFileTypeMask) == UnixSymbolicLink;
}
