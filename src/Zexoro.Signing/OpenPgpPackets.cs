using System.Buffers.Binary;
using System.Text;

namespace Zexoro.Signing;

/// <summary>One OpenPGP packet (RFC 9580 section 4): its tag and body.</summary>
internal readonly record struct OpenPgpPacket(int Tag, ReadOnlyMemory<byte> Body);

/// <summary>
/// Reads OpenPGP data (RFC 9580): ASCII armor (section 6) and the packet framing (section 4.2),
/// both the legacy and the current header format. Partial body lengths are refused: key exports
/// never use them.
/// </summary>
internal static class OpenPgpPackets
{
    public const int SignatureTag = 2;
    public const int SecretKeyTag = 5;
    public const int PublicKeyTag = 6;
    public const int SecretSubkeyTag = 7;
    public const int UserIdTag = 13;
    public const int PublicSubkeyTag = 14;

    /// <summary>
    /// Returns the binary packets of <paramref name="text"/>: the ASCII-armored block whose header
    /// line names <paramref name="blockType"/> (for example <c>PGP PRIVATE KEY BLOCK</c>), with its
    /// CRC-24 checked when present.
    /// </summary>
    /// <exception cref="FormatException">The armor is malformed or its checksum does not match.</exception>
    public static byte[] Dearmor(string text, string blockType)
    {
        var begin = $"-----BEGIN {blockType}-----";
        var end = $"-----END {blockType}-----";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').Select(l => l.Trim()).ToList();
        var start = lines.IndexOf(begin);
        if (start < 0)
        {
            throw new FormatException($"no '{begin}' line");
        }

        var i = start + 1;

        // Armor headers ("Version: …", "Comment: …") end with an empty line.
        while (i < lines.Count && lines[i].Length > 0 && lines[i].Contains(": ", StringComparison.Ordinal))
        {
            i++;
        }

        if (i < lines.Count && lines[i].Length == 0)
        {
            i++;
        }

        var base64 = new StringBuilder();
        string? checksum = null;
        for (; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line == end)
            {
                var data = Convert.FromBase64String(base64.ToString());
                if (checksum is not null)
                {
                    var expected = Convert.FromBase64String(checksum);
                    var actual = Crc24(data);
                    if (expected.Length != 3 || expected[0] != (byte)(actual >> 16) || expected[1] != (byte)(actual >> 8) || expected[2] != (byte)actual)
                    {
                        throw new FormatException("the armor checksum does not match; the key was changed or truncated");
                    }
                }

                return data;
            }

            if (line.StartsWith('=') && line.Length == 5)
            {
                checksum = line[1..];
            }
            else if (line.Length > 0)
            {
                base64.Append(line);
            }
        }

        throw new FormatException($"no '{end}' line");
    }

    /// <summary>The CRC-24 of RFC 9580 section 6.1.1.</summary>
    internal static int Crc24(ReadOnlySpan<byte> data)
    {
        const int Init = 0xB704CE;
        const int Poly = 0x1864CFB;
        var crc = Init;
        foreach (var b in data)
        {
            crc ^= b << 16;
            for (var bit = 0; bit < 8; bit++)
            {
                crc <<= 1;
                if ((crc & 0x1000000) != 0)
                {
                    crc ^= Poly;
                }
            }
        }

        return crc & 0xFFFFFF;
    }

    /// <summary>Splits binary OpenPGP data into packets.</summary>
    /// <exception cref="FormatException">The data is not a sequence of complete packets.</exception>
    public static List<OpenPgpPacket> Read(ReadOnlyMemory<byte> data)
    {
        var packets = new List<OpenPgpPacket>();
        var span = data.Span;
        var position = 0;
        while (position < span.Length)
        {
            var header = span[position++];
            if ((header & 0x80) == 0)
            {
                throw new FormatException($"byte {position - 1} is not an OpenPGP packet header");
            }

            int tag;
            long length;
            if ((header & 0x40) != 0)
            {
                tag = header & 0x3F;
                var first = Byte(span, ref position);
                if (first < 192)
                {
                    length = first;
                }
                else if (first < 224)
                {
                    length = ((first - 192) << 8) + Byte(span, ref position) + 192;
                }
                else if (first == 255)
                {
                    length = BinaryPrimitives.ReadUInt32BigEndian(Bytes(span, ref position, 4));
                }
                else
                {
                    throw new FormatException("partial body lengths are not supported in keys");
                }
            }
            else
            {
                tag = (header >> 2) & 0x0F;
                length = (header & 3) switch
                {
                    0 => Byte(span, ref position),
                    1 => BinaryPrimitives.ReadUInt16BigEndian(Bytes(span, ref position, 2)),
                    2 => BinaryPrimitives.ReadUInt32BigEndian(Bytes(span, ref position, 4)),
                    _ => throw new FormatException("packets of indeterminate length are not supported in keys"),
                };
            }

            if (length > span.Length - position)
            {
                throw new FormatException("a packet is truncated");
            }

            packets.Add(new OpenPgpPacket(tag, data.Slice(position, (int)length)));
            position += (int)length;
        }

        return packets;
    }

    private static byte Byte(ReadOnlySpan<byte> span, ref int position) =>
        position < span.Length ? span[position++] : throw new FormatException("a packet header is truncated");

    private static ReadOnlySpan<byte> Bytes(ReadOnlySpan<byte> span, ref int position, int count)
    {
        if (count > span.Length - position)
        {
            throw new FormatException("a packet header is truncated");
        }

        var result = span.Slice(position, count);
        position += count;
        return result;
    }

    /// <summary>Reads a multiprecision integer (RFC 9580 section 3.2) without leading zero bytes.</summary>
    /// <exception cref="FormatException">The data ends inside it.</exception>
    public static byte[] ReadMpi(ReadOnlySpan<byte> data, ref int position)
    {
        if (data.Length - position < 2)
        {
            throw new FormatException("an MPI is truncated");
        }

        var bits = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
        position += 2;
        var length = (bits + 7) / 8;
        if (data.Length - position < length)
        {
            throw new FormatException("an MPI is truncated");
        }

        var value = data.Slice(position, length);
        position += length;
        var skip = 0;
        while (skip < value.Length - 1 && value[skip] == 0)
        {
            skip++;
        }

        return value[skip..].ToArray();
    }

    /// <summary>Writes <paramref name="value"/> (big-endian, unsigned) as an MPI.</summary>
    public static void WriteMpi(List<byte> output, ReadOnlySpan<byte> value)
    {
        var skip = 0;
        while (skip < value.Length && value[skip] == 0)
        {
            skip++;
        }

        value = value[skip..];
        var bits = value.Length == 0 ? 0 : ((value.Length - 1) * 8) + (32 - System.Numerics.BitOperations.LeadingZeroCount((uint)value[0]));
        output.Add((byte)(bits >> 8));
        output.Add((byte)bits);
        output.AddRange(value.ToArray());
    }
}
