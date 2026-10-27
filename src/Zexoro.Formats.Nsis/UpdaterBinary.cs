using System.Security.Cryptography;
using Zexoro.Platforms;

namespace Zexoro.Formats.Nsis;

/// <summary>
/// A <c>zexoro-updater.exe</c> for one Windows architecture, to be put into installers
/// (docs/design/0005-nsis-updates.md section 3). The installer's architecture picks it.
/// </summary>
/// <param name="Architecture">The architecture it runs on: x86, x64 or arm64.</param>
/// <param name="Source">Where it comes from, for messages.</param>
/// <param name="Sha256">Its expected SHA-256, as 64 lower-case hex digits.</param>
/// <param name="Open">Opens it for reading.</param>
public sealed record UpdaterBinary(Architecture Architecture, string Source, string Sha256, Func<Stream> Open)
{
    /// <summary>The file name inside an install root.</summary>
    public const string FileName = "zexoro-updater.exe";

    /// <summary>Points zexoro at a directory with <c>zexoro-updater-&lt;rid&gt;.exe</c> files, overriding the embedded ones.</summary>
    public const string DirectoryVariable = UpdaterRuntime.DirectoryVariable;

    /// <summary>The architectures an updater is built for.</summary>
    public static IReadOnlyList<Architecture> Architectures { get; } = [Architecture.X86, Architecture.X64, Architecture.Arm64];

    /// <summary>The runtime identifier, e.g. <c>win-x64</c>.</summary>
    public string Rid => RidFor(Architecture);

    public static string RidFor(Architecture architecture) => RuntimeFor(architecture)?.Rid ?? "win-" + architecture.Name;

    /// <summary>The Windows updater build for <paramref name="architecture"/>, or null when there is none.</summary>
    public static UpdaterRuntime? RuntimeFor(Architecture architecture) => UpdaterRuntime.Find(new TargetPlatform(OperatingSystemFamily.Windows, architecture));

    /// <summary>The file name in a directory of updaters (<see cref="DirectoryVariable"/>, the build's <c>ZexoroUpdaterDir</c>).</summary>
    public static string FileNameFor(Architecture architecture) => RuntimeFor(architecture)?.DirectoryFileName ?? $"zexoro-updater-{RidFor(architecture)}.exe";

    /// <summary>
    /// Writes the updater to <paramref name="path"/>, checking its SHA-256 and that it is a Windows
    /// executable for <see cref="Architecture"/>.
    /// </summary>
    /// <exception cref="NsisException">It does not match.</exception>
    /// <exception cref="IOException">It cannot be read or written.</exception>
    public void WriteTo(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var temporary = path + ".tmp";
        try
        {
            using (var source = Open())
            using (var target = File.Create(temporary))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = source.Read(buffer)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    target.Write(buffer, 0, read);
                }

                var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (!string.Equals(actual, Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new NsisException($"The updater for {Rid} ({Source}) has SHA-256 {actual}, expected {Sha256}.");
                }
            }

            var machine = PeMachine.Read(temporary);
            if (machine != Architecture)
            {
                throw new NsisException($"The updater for {Rid} ({Source}) is not a Windows {Architecture.Name} executable.");
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
