using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Zexoro.Toml;

/// <summary>The kind of a <see cref="TomlNode"/>.</summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "The members are the TOML type names.")]
public enum TomlNodeKind
{
    Table,
    Array,
    String,
    Integer,
    Float,
    Boolean,
    OffsetDateTime,
    LocalDateTime,
    LocalDate,
    LocalTime,
}

/// <summary>A key in a table, with the position where it was first written.</summary>
public sealed record TomlKey(string Name, TomlPosition Position);

/// <summary>A table or value in a TOML document, with its source position.</summary>
public abstract class TomlNode
{
    private protected TomlNode(TomlPosition position) => Position = position;

    /// <summary>
    /// Where the node starts in the source: the first character of a value, the opening bracket
    /// of a <c>[table]</c> or <c>[[array]]</c> header, or the key that implicitly created a table.
    /// </summary>
    public TomlPosition Position { get; internal set; }

    public abstract TomlNodeKind Kind { get; }
}

/// <summary>A table: an ordered collection of uniquely named keys.</summary>
[SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix", Justification = "Named after the TOML concept.")]
public sealed class TomlTable : TomlNode, IReadOnlyDictionary<string, TomlNode>
{
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly List<KeyValuePair<TomlKey, TomlNode>> _entries = [];

    internal TomlTable(TomlPosition position, TableState state)
        : base(position) => State = state;

    public override TomlNodeKind Kind => TomlNodeKind.Table;

    /// <summary>Whether the table was written as an inline table (<c>{ ... }</c>).</summary>
    public bool IsInline => State == TableState.Inline;

    internal TableState State { get; set; }

    public int Count => _entries.Count;

    /// <summary>The keys in document order.</summary>
    public IEnumerable<string> Keys => _entries.Select(entry => entry.Key.Name);

    /// <summary>The values in document order.</summary>
    public IEnumerable<TomlNode> Values => _entries.Select(entry => entry.Value);

    /// <summary>The entries in document order, with key positions.</summary>
    public IReadOnlyList<KeyValuePair<TomlKey, TomlNode>> Entries => _entries;

    public TomlNode this[string key] =>
        TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"The table has no key '{key}'.");

    public bool ContainsKey(string key) => _index.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out TomlNode value)
    {
        if (_index.TryGetValue(key, out var i))
        {
            value = _entries[i].Value;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Gets the key with its source position.</summary>
    public bool TryGetKey(string key, [MaybeNullWhen(false)] out TomlKey tomlKey)
    {
        if (_index.TryGetValue(key, out var i))
        {
            tomlKey = _entries[i].Key;
            return true;
        }

        tomlKey = null;
        return false;
    }

    public IEnumerator<KeyValuePair<string, TomlNode>> GetEnumerator()
    {
        foreach (var entry in _entries)
        {
            yield return new KeyValuePair<string, TomlNode>(entry.Key.Name, entry.Value);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void Add(TomlKey key, TomlNode value)
    {
        _index.Add(key.Name, _entries.Count);
        _entries.Add(new KeyValuePair<TomlKey, TomlNode>(key, value));
    }
}

/// <summary>An array of values, or an array of tables built from <c>[[header]]</c> sections.</summary>
public sealed class TomlArray : TomlNode, IReadOnlyList<TomlNode>
{
    private readonly List<TomlNode> _items = [];

    internal TomlArray(TomlPosition position, bool isArrayOfTables)
        : base(position) => IsArrayOfTables = isArrayOfTables;

    public override TomlNodeKind Kind => TomlNodeKind.Array;

    /// <summary>Whether the array was built from <c>[[header]]</c> sections rather than written as a value.</summary>
    public bool IsArrayOfTables { get; }

    public int Count => _items.Count;

    public TomlNode this[int index] => _items[index];

    public IEnumerator<TomlNode> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void Add(TomlNode item) => _items.Add(item);
}

/// <summary>A string value.</summary>
public sealed class TomlString : TomlNode
{
    internal TomlString(TomlPosition position, string value)
        : base(position) => Value = value;

    public override TomlNodeKind Kind => TomlNodeKind.String;

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>A 64-bit signed integer value.</summary>
public sealed class TomlInteger : TomlNode
{
    internal TomlInteger(TomlPosition position, long value)
        : base(position) => Value = value;

    public override TomlNodeKind Kind => TomlNodeKind.Integer;

    public long Value { get; }
}

/// <summary>A 64-bit floating-point value, including infinities and NaN.</summary>
public sealed class TomlFloat : TomlNode
{
    internal TomlFloat(TomlPosition position, double value)
        : base(position) => Value = value;

    public override TomlNodeKind Kind => TomlNodeKind.Float;

    public double Value { get; }
}

/// <summary>A boolean value.</summary>
public sealed class TomlBoolean : TomlNode
{
    internal TomlBoolean(TomlPosition position, bool value)
        : base(position) => Value = value;

    public override TomlNodeKind Kind => TomlNodeKind.Boolean;

    public bool Value { get; }
}

/// <summary>
/// An offset date-time. It is kept as the written local date and time plus the offset, because
/// TOML allows offsets up to ±23:59 and dates near year 1 and 9999, which
/// <see cref="System.DateTimeOffset"/> cannot always represent. Precision is 100 nanoseconds;
/// further digits are truncated.
/// </summary>
public sealed class TomlOffsetDateTime : TomlNode
{
    internal TomlOffsetDateTime(TomlPosition position, DateTime dateTime, TimeSpan offset)
        : base(position)
    {
        DateTime = dateTime;
        Offset = offset;
    }

    public override TomlNodeKind Kind => TomlNodeKind.OffsetDateTime;

    /// <summary>The date and time as written, before applying the offset (kind unspecified).</summary>
    public DateTime DateTime { get; }

    /// <summary>The offset from UTC.</summary>
    public TimeSpan Offset { get; }

    /// <summary>Converts to a <see cref="DateTimeOffset"/>, if the value is within its range.</summary>
    public bool TryGetDateTimeOffset(out DateTimeOffset value)
    {
        var utcTicks = DateTime.Ticks - Offset.Ticks;
        if (Offset.Ticks % TimeSpan.TicksPerMinute == 0
            && Offset >= TimeSpan.FromHours(-14) && Offset <= TimeSpan.FromHours(14)
            && utcTicks >= DateTime.MinValue.Ticks && utcTicks <= DateTime.MaxValue.Ticks)
        {
            value = new DateTimeOffset(DateTime, Offset);
            return true;
        }

        value = default;
        return false;
    }
}

/// <summary>A local date-time, without an offset (kind unspecified). Precision is 100 nanoseconds.</summary>
public sealed class TomlLocalDateTime : TomlNode
{
    internal TomlLocalDateTime(TomlPosition position, DateTime value)
        : base(position) => Value = value;

    public override TomlNodeKind Kind => TomlNodeKind.LocalDateTime;

    public DateTime Value { get; }
}

/// <summary>A local date.</summary>
public sealed class TomlLocalDate : TomlNode
{
    internal TomlLocalDate(TomlPosition position, DateOnly value)
        : base(position) => Value = value;

    public override TomlNodeKind Kind => TomlNodeKind.LocalDate;

    public DateOnly Value { get; }
}

/// <summary>A local time of day. Precision is 100 nanoseconds.</summary>
public sealed class TomlLocalTime : TomlNode
{
    internal TomlLocalTime(TomlPosition position, TimeOnly value)
        : base(position) => Value = value;

    public override TomlNodeKind Kind => TomlNodeKind.LocalTime;

    public TimeOnly Value { get; }
}

/// <summary>How a table came to exist; decides which later definitions are allowed.</summary>
internal enum TableState
{
    /// <summary>The root table.</summary>
    Root,

    /// <summary>Created as a parent in a header path; may still be defined by its own header once.</summary>
    Implicit,

    /// <summary>Defined by a <c>[table]</c> header.</summary>
    Header,

    /// <summary>An element of an array of tables, created by a <c>[[table]]</c> header.</summary>
    ArrayElement,

    /// <summary>Created by a dotted key; may be extended by dotted keys in the same section.</summary>
    Dotted,

    /// <summary>An inline table or a table created by dotted keys inside one; closed for additions.</summary>
    Inline,
}
