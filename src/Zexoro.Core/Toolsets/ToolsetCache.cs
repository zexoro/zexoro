using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Zexoro.Toolsets;

/// <summary>
/// Extracts bundled toolset archives into a per-user cache directory, once, and verifies them
/// before each use.
/// </summary>
/// <remarks>
/// Layout: <c>&lt;root&gt;/toolsets/&lt;name&gt;-&lt;version&gt;-&lt;hash prefix&gt;/</c>, holding the extracted
/// files and a manifest (<see cref="ManifestFileName"/>) with the SHA-256 of every file. An archive
/// is extracted into a temporary sibling directory and renamed into place, so other processes see
/// either nothing or a complete directory; when two processes extract at once, the first rename
/// wins and the other uses the winner's result. A directory that fails verification is moved aside
/// and extracted again.
/// </remarks>
public static class ToolsetCache
{
    /// <summary>The manifest written into every extracted toolset directory.</summary>
    public const string ManifestFileName = ".zexoro-toolset";

    /// <summary>Overrides the cache root (all platforms).</summary>
    public const string CacheDirectoryVariable = "ZEXORO_CACHE_DIR";

    private const string ManifestHeader = "zexoro-toolset 1";
    private const int MaxAttempts = 5;
    private const long MaxArchiveSize = 512L * 1024 * 1024;

    /// <summary>
    /// The per-user cache root: <c>$ZEXORO_CACHE_DIR</c> if set; otherwise <c>%LOCALAPPDATA%\zexoro</c>
    /// on Windows, <c>~/Library/Caches/zexoro</c> on macOS and <c>$XDG_CACHE_HOME/zexoro</c> (default
    /// <c>~/.cache/zexoro</c>) elsewhere.
    /// </summary>
    public static string GetDefaultRoot(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        if (Absolute(getVariable(CacheDirectoryVariable)) is { } overridden)
        {
            return overridden;
        }

        if (OperatingSystem.IsWindows())
        {
            var local = Absolute(getVariable("LOCALAPPDATA"))
                ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "zexoro");
        }

        var home = Absolute(getVariable("HOME")) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Caches", "zexoro");
        }

        return Path.Combine(Absolute(getVariable("XDG_CACHE_HOME")) ?? Path.Combine(home, ".cache"), "zexoro");
    }

    /// <summary>
    /// Returns the directory holding the verified, extracted toolset, extracting it first if it is
    /// missing or does not match its manifest.
    /// </summary>
    /// <param name="archive">The archive to extract.</param>
    /// <param name="cacheRoot">The cache root, e.g. from <see cref="GetDefaultRoot"/>.</param>
    /// <param name="include">
    /// Selects archive entries by relative path (forward slashes); null extracts everything. Part of
    /// the cache identity only through the archive: callers must use the same filter for the same archive.
    /// </param>
    /// <exception cref="ToolsetException">The archive does not match its hash, is malformed, or cannot be extracted.</exception>
    public static string Install(ToolsetArchive archive, string cacheRoot, Func<string, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(cacheRoot);
        archive.Validate();

        var parent = Path.Combine(Path.GetFullPath(cacheRoot), "toolsets");
        var target = Path.Combine(parent, archive.DirectoryName);
        for (var attempt = 1; ; attempt++)
        {
            if (Directory.Exists(target))
            {
                if (Verify(target, archive, out _))
                {
                    return target;
                }

                MoveAside(target, parent);
            }

            var temp = Path.Combine(parent, $".tmp-{Guid.NewGuid():N}");
            try
            {
                Extract(archive, temp, include);
                try
                {
                    Directory.Move(temp, target);
                    return target;
                }
                catch (IOException) when (Directory.Exists(target) && attempt < MaxAttempts)
                {
                    // Another process finished first; verify and use its result on the next pass.
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < MaxAttempts)
                {
                    // A transient failure (for example a virus scanner holding a file); retry.
                    Thread.Sleep(100 * attempt);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ToolsetException($"Cannot extract the {archive.Name} toolset into {parent}: {ex.Message}", ex);
            }
            finally
            {
                TryDelete(temp);
            }
        }
    }

    /// <summary>
    /// Checks an extracted toolset directory against its manifest: the manifest names this archive,
    /// and every listed file exists with the recorded hash (and, on Unix, executable bits).
    /// </summary>
    public static bool Verify(string directory, ToolsetArchive archive, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(archive);
        try
        {
            problem = VerifyCore(directory, archive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
        }

        return problem is null;
    }

    private static string? VerifyCore(string directory, ToolsetArchive archive)
    {
        var manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return $"{ManifestFileName} is missing";
        }

        var lines = File.ReadAllLines(manifestPath, Encoding.UTF8);
        if (lines.Length < 2 || lines[0] != ManifestHeader || lines[1] != $"{archive.Name} {archive.Version} {archive.Sha256}")
        {
            return $"{ManifestFileName} does not describe {archive.Name} {archive.Version}";
        }

        foreach (var line in lines.Skip(2))
        {
            var parts = line.Split(' ', 3);
            if (parts.Length != 3)
            {
                return $"{ManifestFileName} is malformed";
            }

            var path = Path.Combine(directory, parts[2].Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                return $"{parts[2]} is missing";
            }

            if (HashFile(path) != parts[0])
            {
                return $"{parts[2]} has been modified";
            }

            if (!OperatingSystem.IsWindows()
                && int.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var mode)
                && ((int)File.GetUnixFileMode(path) & 0b001_001_001) != (mode & 0b001_001_001))
            {
                return $"{parts[2]} has lost its executable permission";
            }
        }

        return null;
    }

    private static void Extract(ToolsetArchive archive, string destination, Func<string, bool>? include)
    {
        using var buffer = ReadVerified(archive);
        Directory.CreateDirectory(destination);
        var manifest = new StringBuilder();
        manifest.Append(ManifestHeader).Append('\n');
        manifest.Append(CultureInfo.InvariantCulture, $"{archive.Name} {archive.Version} {archive.Sha256}").Append('\n');

        try
        {
            using var gzip = new GZipStream(buffer, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            while (reader.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is TarEntryType.GlobalExtendedAttributes)
                {
                    continue;
                }

                var relative = NormalizeEntryName(archive, entry.Name);
                if (relative is null || (include is not null && !include(relative)))
                {
                    continue;
                }

                var path = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        Directory.CreateDirectory(path);
                        break;
                    case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        var hash = WriteFile(entry, path);
                        var mode = (int)entry.Mode & 0b111_111_111;
                        if (!OperatingSystem.IsWindows())
                        {
                            File.SetUnixFileMode(path, (UnixFileMode)(mode | 0b110_000_000));
                        }

                        manifest.Append(CultureInfo.InvariantCulture, $"{hash} {mode:x3} {relative}").Append('\n');
                        break;
                    default:
                        throw new ToolsetException($"The {archive.Name} toolset archive contains '{entry.Name}' of type {entry.EntryType}, which is not supported.");
                }
            }
        }
        catch (InvalidDataException ex)
        {
            throw new ToolsetException($"The {archive.Name} toolset archive is malformed: {ex.Message}", ex);
        }

        // The manifest is written last: a directory without one is never treated as complete.
        File.WriteAllText(Path.Combine(destination, ManifestFileName), manifest.ToString(), new UTF8Encoding(false));
    }

    private static MemoryStream ReadVerified(ToolsetArchive archive)
    {
        var buffer = new MemoryStream();
        try
        {
            using (var source = archive.Open())
            {
                source.CopyTo(buffer);
            }

            if (buffer.Length > MaxArchiveSize)
            {
                throw new ToolsetException($"The {archive.Name} toolset archive is larger than {MaxArchiveSize} bytes.");
            }

            var actual = Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
            if (actual != archive.Sha256)
            {
                throw new ToolsetException($"The {archive.Name} {archive.Version} toolset archive is corrupt: its SHA-256 is {actual}, expected {archive.Sha256}.");
            }

            buffer.Position = 0;
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    private static string? NormalizeEntryName(ToolsetArchive archive, string name)
    {
        var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToList();
        if (segments.Count == 0)
        {
            return null;
        }

        if (name.StartsWith('/') || name.Contains('\\', StringComparison.Ordinal) || segments.Any(s => s == ".." || s.Contains(':', StringComparison.Ordinal)))
        {
            throw new ToolsetException($"The {archive.Name} toolset archive contains an unsafe path: '{name}'.");
        }

        var relative = string.Join('/', segments);
        return relative == ManifestFileName
            ? throw new ToolsetException($"The {archive.Name} toolset archive contains a reserved file name: '{name}'.")
            : relative;
    }

    private static string WriteFile(TarEntry entry, string path)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (entry.DataStream is { } data)
        {
            var chunk = new byte[81920];
            int read;
            while ((read = data.Read(chunk)) > 0)
            {
                output.Write(chunk, 0, read);
                hash.AppendData(chunk, 0, read);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void MoveAside(string directory, string parent)
    {
        var stale = Path.Combine(parent, $".stale-{Guid.NewGuid():N}");
        try
        {
            Directory.Move(directory, stale);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another process moved or replaced it already; the next pass looks again.
            return;
        }

        TryDelete(stale);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for a later run; temporary directories are never used as toolsets.
        }
    }

    private static string? Absolute(string? path) =>
        string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) ? null : path;
}
