using System.Buffers.Binary;

namespace Zexoro.Platforms;

/// <summary>Reads the target architecture of a Windows executable from its COFF header.</summary>
public static class PeMachine
{
    /// <summary>
    /// Returns the architecture of the PE file at <paramref name="path"/>, or null when it is not a
    /// PE file or targets an architecture zexoro does not support (x86, x64 and arm64 are).
    /// </summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static Architecture? Read(string path)
    {
        using var stream = File.OpenRead(path);
        return TryReadMachine(stream, out var machine) ? ToArchitecture(machine) : null;
    }

    /// <summary>Maps a COFF machine type to an architecture, or null for one zexoro does not support.</summary>
    internal static Architecture? ToArchitecture(ushort machine) => machine switch
    {
        0x014C => Architecture.X86,
        0x8664 => Architecture.X64,
        0xAA64 => Architecture.Arm64,
        _ => null,
    };

    /// <summary>Reads the COFF machine type when <paramref name="stream"/> (positioned anywhere) holds a PE image.</summary>
    internal static bool TryReadMachine(Stream stream, out ushort machine)
    {
        machine = 0;
        stream.Position = 0;
        Span<byte> dos = stackalloc byte[64];
        if (stream.ReadAtLeast(dos, dos.Length, throwOnEndOfStream: false) < dos.Length || dos[0] != 'M' || dos[1] != 'Z')
        {
            return false;
        }

        var offset = BinaryPrimitives.ReadInt32LittleEndian(dos[0x3C..]);
        if (offset < 64 || offset > stream.Length - 6)
        {
            return false;
        }

        stream.Position = offset;
        Span<byte> header = stackalloc byte[6];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length || !header[..4].SequenceEqual("PE\0\0"u8))
        {
            return false;
        }

        machine = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        return true;
    }
}
