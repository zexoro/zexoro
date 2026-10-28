using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml;

namespace Zexoro.Formats.MacOS;

/// <summary>A property list cannot be read; the message is for the user.</summary>
public sealed class PropertyListException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// Property lists (Info.plist): writes XML, reads XML and binary (<c>bplist00</c>). Values are
/// <see cref="string"/>, <see cref="bool"/>, <see cref="long"/>, <see cref="double"/>,
/// <see cref="DateTimeOffset"/>, <see cref="byte"/> arrays, <see cref="List{T}"/> of values and
/// dictionaries (<see cref="Dictionary{TKey, TValue}"/> with string keys).
/// </summary>
public static class PropertyList
{
    private const int MaxDepth = 64;

    /// <summary>
    /// Writes <paramref name="root"/> as an XML property list, keys sorted ordinally and indented
    /// with tabs, as Xcode writes Info.plist; UTF-8 without a byte order mark, LF line endings.
    /// </summary>
    /// <exception cref="ArgumentException">A value has an unsupported type.</exception>
    public static byte[] ToXml(IReadOnlyDictionary<string, object> root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        builder.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        builder.Append("<plist version=\"1.0\">\n");
        WriteValue(builder, root, 0);
        builder.Append("</plist>\n");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>Reads an XML or binary property list whose root is a dictionary.</summary>
    /// <exception cref="PropertyListException">The bytes are not a valid property list with a dictionary root.</exception>
    public static Dictionary<string, object> Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var root = bytes.AsSpan().StartsWith("bplist00"u8) ? new BinaryReader(bytes).ReadRoot() : ReadXml(bytes);
        return root as Dictionary<string, object> ?? throw new PropertyListException("the property list's root is not a dictionary");
    }

    private static void WriteValue(StringBuilder builder, object value, int depth)
    {
        var indent = new string('\t', depth);
        switch (value)
        {
            case string text:
                builder.Append(indent).Append("<string>").Append(Escape(text)).Append("</string>\n");
                break;
            case bool flag:
                builder.Append(indent).Append(flag ? "<true/>" : "<false/>").Append('\n');
                break;
            case long or int:
                builder.Append(indent).Append("<integer>").Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)).Append("</integer>\n");
                break;
            case double real:
                builder.Append(indent).Append("<real>").Append(real.ToString("R", CultureInfo.InvariantCulture)).Append("</real>\n");
                break;
            case DateTimeOffset date:
                builder.Append(indent).Append("<date>").Append(date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)).Append("</date>\n");
                break;
            case byte[] data:
                builder.Append(indent).Append("<data>").Append(Convert.ToBase64String(data)).Append("</data>\n");
                break;
            case IReadOnlyDictionary<string, object> dictionary:
                builder.Append(indent).Append("<dict>\n");
                foreach (var key in dictionary.Keys.Order(StringComparer.Ordinal))
                {
                    builder.Append(indent).Append('\t').Append("<key>").Append(Escape(key)).Append("</key>\n");
                    WriteValue(builder, dictionary[key], depth + 1);
                }

                builder.Append(indent).Append("</dict>\n");
                break;
            case IEnumerable<object> array:
                builder.Append(indent).Append("<array>\n");
                foreach (var item in array)
                {
                    WriteValue(builder, item, depth + 1);
                }

                builder.Append(indent).Append("</array>\n");
                break;
            default:
                throw new ArgumentException($"A property list cannot hold a {value?.GetType().Name ?? "null"} value.", nameof(value));
        }
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static object ReadXml(byte[] bytes)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
        };
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, settings);
            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "plist")
            {
                throw new PropertyListException("not a property list (no <plist> element)");
            }

            if (reader.IsEmptyElement || !reader.Read())
            {
                throw new PropertyListException("the property list is empty");
            }

            reader.MoveToContent();
            return ReadXmlValue(reader, 0);
        }
        catch (XmlException ex)
        {
            throw new PropertyListException($"not a valid XML property list: {ex.Message}", ex);
        }
        catch (FormatException ex)
        {
            throw new PropertyListException($"not a valid XML property list: {ex.Message}", ex);
        }
    }

    /// <summary>Reads the element the reader is on and leaves the reader after its end.</summary>
    private static object ReadXmlValue(XmlReader reader, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new PropertyListException("the property list is nested too deeply");
        }

        if (reader.NodeType != XmlNodeType.Element)
        {
            throw new PropertyListException($"unexpected {reader.NodeType} in the property list");
        }

        switch (reader.Name)
        {
            case "dict":
            {
                var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
                if (reader.IsEmptyElement)
                {
                    reader.Read();
                    return dictionary;
                }

                reader.Read();
                while (reader.NodeType != XmlNodeType.EndElement)
                {
                    if (reader.NodeType != XmlNodeType.Element || reader.Name != "key")
                    {
                        throw new PropertyListException("a <dict> entry without a <key>");
                    }

                    var key = reader.ReadElementContentAsString();
                    reader.MoveToContent();
                    dictionary[key] = ReadXmlValue(reader, depth + 1);
                    reader.MoveToContent();
                }

                reader.Read();
                return dictionary;
            }

            case "array":
            {
                var list = new List<object>();
                if (reader.IsEmptyElement)
                {
                    reader.Read();
                    return list;
                }

                reader.Read();
                reader.MoveToContent();
                while (reader.NodeType != XmlNodeType.EndElement)
                {
                    list.Add(ReadXmlValue(reader, depth + 1));
                    reader.MoveToContent();
                }

                reader.Read();
                return list;
            }

            case "true":
            case "false":
            {
                var value = reader.Name == "true";
                reader.Skip();
                return value;
            }

            case "string":
                return reader.ReadElementContentAsString();
            case "integer":
                return long.Parse(reader.ReadElementContentAsString().Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            case "real":
                return double.Parse(reader.ReadElementContentAsString().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
            case "date":
                return DateTimeOffset.Parse(reader.ReadElementContentAsString().Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            case "data":
                return Convert.FromBase64String(string.Concat(reader.ReadElementContentAsString().Where(c => !char.IsWhiteSpace(c))));
            default:
                throw new PropertyListException($"unknown property list element <{reader.Name}>");
        }
    }

    /// <summary>Reads <c>bplist00</c>: objects by marker byte, an offset table and a 32-byte trailer.</summary>
    private sealed class BinaryReader(byte[] bytes)
    {
        private int _offsetSize;
        private int _referenceSize;
        private long _objectCount;
        private long _offsetTable;

        public object ReadRoot()
        {
            if (bytes.Length < 8 + 32)
            {
                throw new PropertyListException("a truncated binary property list");
            }

            var trailer = bytes.AsSpan(bytes.Length - 32);
            _offsetSize = trailer[6];
            _referenceSize = trailer[7];
            _objectCount = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[8..]);
            var top = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[16..]);
            _offsetTable = (long)BinaryPrimitives.ReadUInt64BigEndian(trailer[24..]);
            if (_offsetSize is < 1 or > 8 || _referenceSize is < 1 or > 8 || _objectCount <= 0 || top < 0 || top >= _objectCount
                || _offsetTable < 8 || _offsetTable + (_objectCount * _offsetSize) > bytes.Length - 32)
            {
                throw new PropertyListException("a binary property list with an invalid trailer");
            }

            return ReadObject(top, 0);
        }

        private object ReadObject(long index, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new PropertyListException("the property list is nested too deeply");
            }

            if (index < 0 || index >= _objectCount)
            {
                throw new PropertyListException("a binary property list with an invalid object reference");
            }

            var offset = (long)ReadUnsigned(_offsetTable + (index * _offsetSize), _offsetSize);
            if (offset < 8 || offset >= _offsetTable)
            {
                throw new PropertyListException("a binary property list with an invalid object offset");
            }

            var marker = bytes[offset];
            var high = marker >> 4;
            var low = marker & 0xF;
            switch (high)
            {
                case 0x0 when low == 0x8:
                    return false;
                case 0x0 when low == 0x9:
                    return true;
                case 0x1:
                {
                    var size = 1 << low;
                    if (size > 8)
                    {
                        throw new PropertyListException("a binary property list with an integer wider than 64 bits");
                    }

                    // Eight-byte integers are signed; shorter ones unsigned.
                    return (long)ReadUnsigned(offset + 1, size);
                }

                case 0x2:
                {
                    var size = 1 << low;
                    Check(offset + 1, size);
                    return size switch
                    {
                        4 => (double)BinaryPrimitives.ReadSingleBigEndian(bytes.AsSpan((int)offset + 1)),
                        8 => BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan((int)offset + 1)),
                        _ => throw new PropertyListException("a binary property list with an invalid real"),
                    };
                }

                case 0x3 when low == 0x3:
                {
                    Check(offset + 1, 8);
                    var seconds = BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan((int)offset + 1));
                    return new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);
                }

                case 0x4:
                {
                    var (count, start) = ReadCount(offset, low);
                    Check(start, count);
                    return bytes.AsSpan((int)start, (int)count).ToArray();
                }

                case 0x5:
                {
                    var (count, start) = ReadCount(offset, low);
                    Check(start, count);
                    return Encoding.ASCII.GetString(bytes, (int)start, (int)count);
                }

                case 0x6:
                {
                    var (count, start) = ReadCount(offset, low);
                    Check(start, count * 2);
                    return Encoding.BigEndianUnicode.GetString(bytes, (int)start, (int)(count * 2));
                }

                case 0xA:
                {
                    var (count, start) = ReadCount(offset, low);
                    Check(start, count * _referenceSize);
                    var list = new List<object>();
                    for (long i = 0; i < count; i++)
                    {
                        list.Add(ReadObject((long)ReadUnsigned(start + (i * _referenceSize), _referenceSize), depth + 1));
                    }

                    return list;
                }

                case 0xD:
                {
                    var (count, start) = ReadCount(offset, low);
                    Check(start, count * 2 * _referenceSize);
                    var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
                    for (long i = 0; i < count; i++)
                    {
                        var key = ReadObject((long)ReadUnsigned(start + (i * _referenceSize), _referenceSize), depth + 1) as string
                            ?? throw new PropertyListException("a binary property list with a dictionary key that is not a string");
                        dictionary[key] = ReadObject((long)ReadUnsigned(start + ((count + i) * _referenceSize), _referenceSize), depth + 1);
                    }

                    return dictionary;
                }

                default:
                    throw new PropertyListException($"a binary property list with an unsupported object (marker 0x{marker:X2})");
            }
        }

        /// <summary>The count of a data, string, array or dictionary object, and where its content starts.</summary>
        private (long Count, long Start) ReadCount(long offset, int low)
        {
            if (low != 0xF)
            {
                return (low, offset + 1);
            }

            Check(offset + 1, 1);
            var marker = bytes[offset + 1];
            if (marker >> 4 != 0x1 || (marker & 0xF) > 3)
            {
                throw new PropertyListException("a binary property list with an invalid object length");
            }

            var size = 1 << (marker & 0xF);
            var count = ReadUnsigned(offset + 2, size);
            if (count > int.MaxValue)
            {
                throw new PropertyListException("a binary property list with an invalid object length");
            }

            return ((long)count, offset + 2 + size);
        }

        private ulong ReadUnsigned(long offset, int size)
        {
            Check(offset, size);
            ulong value = 0;
            for (var i = 0; i < size; i++)
            {
                value = (value << 8) | bytes[offset + i];
            }

            return value;
        }

        private void Check(long offset, long length)
        {
            if (offset < 0 || length < 0 || offset + length > bytes.Length)
            {
                throw new PropertyListException("a truncated binary property list");
            }
        }
    }
}
