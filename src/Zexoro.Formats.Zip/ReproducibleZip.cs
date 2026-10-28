using System.Buffers.Binary;
using System.IO.Compression;

namespace Zexoro.Formats.Zip;

/// <summary>
/// Writes zip archives whose bytes depend only on the files' names and contents, not on the host,
/// the time or the file system: entries sorted by name (ordinal), every timestamp
/// <see cref="Timestamp"/>, MS-DOS attributes only (directory or none) and "made by" MS-DOS in the
/// central directory. <see cref="ZipArchive"/> otherwise records the host (Unix or Windows) and,
/// on Unix, Unix permissions, so the same input would give different archives on different hosts.
/// </summary>
public static class ReproducibleZip
{
    /// <summary>
    /// The timestamp of every entry: 1980-02-01 00:00, the start of the MS-DOS date range plus a
    /// month, so tools that convert the zone-less MS-DOS time to UTC never get a date before 1980.
    /// </summary>
    public static DateTimeOffset Timestamp { get; } = new(1980, 2, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The MS-DOS directory attribute.</summary>
    internal const int DirectoryAttribute = 0x10;

    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064b50;
    private const uint CentralHeaderSignature = 0x02014b50;

    /// <summary>
    /// Writes every file and directory under <paramref name="source"/> to the archive
    /// <paramref name="destination"/>, below one top-level directory <paramref name="prefix"/>.
    /// Directory entries are written for every directory, so empty ones survive.
    /// </summary>
    /// <exception cref="IOException">A file cannot be read or the archive cannot be written.</exception>
    public static void WriteDirectory(string source, string prefix, string destination)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        if (prefix.Contains('/', StringComparison.Ordinal) || prefix.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("The prefix must be a single path segment.", nameof(prefix));
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
        var entries = new List<(string Name, string? Path)> { (prefix + "/", null) };
        foreach (var directory in Directory.EnumerateDirectories(source, "*", options))
        {
            entries.Add((prefix + "/" + Relative(source, directory) + "/", null));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", options))
        {
            entries.Add((prefix + "/" + Relative(source, file), file));
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        using var stream = new FileStream(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, path) in entries)
            {
                if (path is null)
                {
                    var directory = archive.CreateEntry(name, CompressionLevel.NoCompression);
                    directory.LastWriteTime = Timestamp;
                    directory.ExternalAttributes = DirectoryAttribute;
                    continue;
                }

                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = Timestamp;
                entry.ExternalAttributes = 0;
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }

        MarkMadeByMsDos(stream);
    }

    /// <summary>
    /// Sets the "version made by" host of every central directory header (and of the Zip64 end
    /// record, if any) to 0, MS-DOS: the attributes are MS-DOS attributes on every host.
    /// </summary>
    /// <exception cref="InvalidDataException">The archive's end records are not where they must be.</exception>
    internal static void MarkMadeByMsDos(Stream stream) => MarkMadeBy(stream, 0);

    /// <summary>
    /// Sets the "version made by" host of every central directory header (and of the Zip64 end
    /// record, if any) to <paramref name="host"/>: 0 for MS-DOS attributes, 3 for Unix modes in the
    /// external attributes' high 16 bits.
    /// </summary>
    /// <exception cref="InvalidDataException">The archive's end records are not where they must be.</exception>
    internal static void MarkMadeBy(Stream stream, byte host)
    {
        var length = stream.Length;
        var tailLength = (int)Math.Min(length, 22 + ushort.MaxValue);
        var tail = new byte[tailLength];
        stream.Position = length - tailLength;
        stream.ReadExactly(tail);
        var end = -1;
        for (var i = tailLength - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EndOfCentralDirectorySignature)
            {
                end = i;
                break;
            }
        }

        if (end < 0)
        {
            throw new InvalidDataException("The archive has no end of central directory record.");
        }

        long count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(end + 10));
        long size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12));
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));
        if (count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
        {
            var locator = end - 20;
            if (locator < 0 || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locator)) != Zip64LocatorSignature)
            {
                throw new InvalidDataException("The archive has no Zip64 end of central directory locator.");
            }

            var zip64End = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(locator + 8));
            var record = new byte[56];
            stream.Position = zip64End;
            stream.ReadExactly(record);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Zip64EndOfCentralDirectorySignature)
            {
                throw new InvalidDataException("The archive has no Zip64 end of central directory record.");
            }

            count = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32));
            size = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40));
            offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(48));
            stream.Position = zip64End + 13;
            stream.WriteByte(host);
        }

        var directory = new byte[size];
        stream.Position = offset;
        stream.ReadExactly(directory);
        var position = 0;
        for (long i = 0; i < count; i++)
        {
            if (position + 46 > directory.Length || BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(position)) != CentralHeaderSignature)
            {
                throw new InvalidDataException("The archive's central directory is malformed.");
            }

            directory[position + 5] = host;
            position += 46
                + BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 28))
                + BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 30))
                + BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 32));
        }

        stream.Position = offset;
        stream.Write(directory);
        stream.Flush();
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
