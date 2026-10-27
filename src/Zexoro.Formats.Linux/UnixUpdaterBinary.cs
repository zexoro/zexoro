using System.Security.Cryptography;
using Zexoro.Platforms;

namespace Zexoro.Formats.Linux;

/// <summary>A zexoro-updater for Linux (or macOS), to be put into a package.</summary>
/// <param name="Runtime">The build.</param>
/// <param name="Source">Where it comes from, for messages.</param>
/// <param name="Sha256">Its expected SHA-256, as 64 lower-case hex digits.</param>
/// <param name="Open">Opens it for reading.</param>
public sealed record UnixUpdaterBinary(UpdaterRuntime Runtime, string Source, string Sha256, Func<Stream> Open)
{
    /// <summary>
    /// Reads the updater, checking its SHA-256 and that its header names <see cref="Runtime"/>'s
    /// operating system and architecture.
    /// </summary>
    /// <exception cref="LinuxPackException">It does not match.</exception>
    /// <exception cref="IOException">It cannot be read.</exception>
    public byte[] ReadVerified()
    {
        byte[] bytes;
        using (var source = Open())
        using (var buffer = new MemoryStream())
        {
            source.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(actual, Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new LinuxPackException($"The updater for {Runtime.Rid} ({Source}) has SHA-256 {actual}, expected {Sha256}.");
        }

        using var stream = new MemoryStream(bytes, writable: false);
        if (ExecutableInspector.Inspect(stream).Target != Runtime.Platform)
        {
            throw new LinuxPackException($"The updater for {Runtime.Rid} ({Source}) is not a {Runtime.Platform} executable.");
        }

        return bytes;
    }
}
