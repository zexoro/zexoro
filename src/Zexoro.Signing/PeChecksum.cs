using System.Buffers.Binary;

namespace Zexoro.Signing;

/// <summary>
/// The PE image checksum, as computed by <c>CheckSumMappedFile</c> in imagehlp: the one's
/// complement sum of all 16-bit little-endian words of the file (the <c>CheckSum</c> field read as
/// zero, a trailing odd byte as a word of its own), folded to 16 bits, plus the file length.
/// </summary>
internal static class PeChecksum
{
    private const int BufferSize = 64 * 1024;

    public static uint Compute(Stream stream, long checkSumOffset)
    {
        var length = stream.Length;
        var buffer = new byte[BufferSize];
        ulong sum = 0;
        long position = 0;
        stream.Position = 0;

        while (position < length)
        {
            var count = (int)Math.Min(buffer.Length, length - position);
            stream.ReadExactly(buffer, 0, count);

            // The checksum field itself counts as zero.
            for (var i = 0; i < 4; i++)
            {
                var index = checkSumOffset + i - position;
                if (index >= 0 && index < count)
                {
                    buffer[index] = 0;
                }
            }

            // BufferSize is even, so only the last chunk can end in an odd byte.
            var words = count / 2;
            for (var i = 0; i < words; i++)
            {
                sum += BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i * 2));
            }

            if ((count & 1) != 0)
            {
                sum += buffer[count - 1];
            }

            position += count;
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return unchecked((uint)sum + (uint)length);
    }
}
