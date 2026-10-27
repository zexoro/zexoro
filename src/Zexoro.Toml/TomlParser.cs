using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Unicode;

namespace Zexoro.Toml;

/// <summary>Parses TOML 1.1.0 documents into a <see cref="TomlTable"/> tree.</summary>
public static class TomlParser
{
    /// <summary>Parses a TOML document.</summary>
    /// <returns>The root table.</returns>
    /// <exception cref="TomlException">The document is not valid TOML 1.1.0.</exception>
    public static TomlTable Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Parser(text).ParseDocument();
    }

    /// <summary>Parses a UTF-8 encoded TOML document. A leading byte order mark is ignored.</summary>
    /// <returns>The root table.</returns>
    /// <exception cref="TomlException">The bytes are not valid UTF-8 or not valid TOML 1.1.0.</exception>
    public static TomlTable Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            utf8 = utf8[3..];
        }

        var chars = new char[utf8.Length];
        var status = Utf8.ToUtf16(utf8, chars, out var bytesRead, out var charsWritten, replaceInvalidSequences: false);
        if (status != OperationStatus.Done)
        {
            var prefix = new string(chars, 0, charsWritten);
            var position = new Parser(prefix).PositionAt(prefix.Length);
            throw new TomlException(
                $"The document is not valid UTF-8 (invalid byte 0x{utf8[bytesRead]:X2}).", position);
        }

        return Parse(new string(chars, 0, charsWritten));
    }

    /// <summary>Reads and parses a UTF-8 encoded TOML file.</summary>
    /// <exception cref="TomlException">The file is not valid TOML 1.1.0.</exception>
    public static TomlTable ParseFile(string path) => Parse(File.ReadAllBytes(path));
}

internal sealed class Parser
{
    private const int MaxNesting = 256;

    private readonly string _s;
    private readonly List<int> _lineStarts = [0];
    private readonly TomlTable _root = new(new TomlPosition(1, 1), TableState.Root);
    private TomlTable _current;
    private int _pos;
    private int _depth;

    public Parser(string text)
    {
        _s = text;
        _current = _root;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                _lineStarts.Add(i + 1);
            }
        }
    }

    private bool Eof => _pos >= _s.Length;

    private char Cur => _s[_pos];

    public TomlPosition PositionAt(int index)
    {
        var line = _lineStarts.BinarySearch(index);
        if (line < 0)
        {
            line = ~line - 1;
        }

        var start = _lineStarts[line];
        var column = 1;
        for (var i = start; i < index && i < _s.Length; i++)
        {
            if (!char.IsLowSurrogate(_s[i]) || i == start || !char.IsHighSurrogate(_s[i - 1]))
            {
                column++;
            }
        }

        return new TomlPosition(line + 1, column);
    }

    public TomlTable ParseDocument()
    {
        if (_s.Length > 0 && _s[0] == '﻿')
        {
            _pos = 1;
        }

        CheckSurrogates();

        while (true)
        {
            SkipWhitespace();
            if (Eof)
            {
                break;
            }

            var c = Cur;
            if (c == '[')
            {
                ParseHeader();
            }
            else if (c != '#' && c != '\n' && c != '\r')
            {
                ParseKeyValue(_current);
            }

            SkipWhitespace();
            if (!Eof && Cur == '#')
            {
                SkipComment();
            }

            if (Eof)
            {
                break;
            }

            ExpectNewline();
        }

        return _root;
    }

    private void CheckSurrogates()
    {
        for (var i = 0; i < _s.Length; i++)
        {
            if (char.IsHighSurrogate(_s[i]) && i + 1 < _s.Length && char.IsLowSurrogate(_s[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(_s[i]))
            {
                throw Error(i, "The document contains an unpaired UTF-16 surrogate, which is not a Unicode character.");
            }
        }
    }

    // ---- Lexical helpers -------------------------------------------------------------------

    private TomlException Error(int index, string message) => new(message, PositionAt(index));

    private TomlException Error(string message) => Error(_pos, message);

    private char At(int index) => index < _s.Length ? _s[index] : '\0';

    private bool AtEnd(int index) => index >= _s.Length;

    private static bool IsControl(char c) => (c < 0x20 && c != '\t') || c == 0x7F;

    private static bool IsBareKeyChar(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private bool IsDigits(int index, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (!IsDigit(At(index + i)) || AtEnd(index + i))
            {
                return false;
            }
        }

        return true;
    }

    private string Describe(int index)
    {
        if (AtEnd(index))
        {
            return "end of file";
        }

        var c = _s[index];
        return c switch
        {
            '\n' or '\r' => "end of line",
            _ when char.IsHighSurrogate(c) && char.IsLowSurrogate(At(index + 1)) =>
                $"'{_s.Substring(index, 2)}'",
            _ when c < 0x20 || c == 0x7F || char.IsWhiteSpace(c) || char.IsControl(c) || c == '﻿' =>
                $"U+{(int)c:X4}",
            _ => $"'{c}'",
        };
    }

    private void SkipWhitespace()
    {
        while (!Eof && (Cur == ' ' || Cur == '\t'))
        {
            _pos++;
        }
    }

    private void SkipComment()
    {
        _pos++; // '#'
        while (!Eof)
        {
            var c = Cur;
            if (c == '\n' || (c == '\r' && At(_pos + 1) == '\n' && !AtEnd(_pos + 1)))
            {
                return;
            }

            if (IsControl(c))
            {
                throw Error($"Control character U+{(int)c:X4} is not allowed in comments.");
            }

            _pos++;
        }
    }

    private void ExpectNewline()
    {
        if (Cur == '\n')
        {
            _pos++;
        }
        else if (Cur == '\r' && At(_pos + 1) == '\n' && !AtEnd(_pos + 1))
        {
            _pos += 2;
        }
        else if (Cur == '\r')
        {
            throw Error("A carriage return must be followed by a line feed.");
        }
        else
        {
            throw Error($"Expected the end of the line, but found {Describe(_pos)}.");
        }
    }

    /// <summary>Skips whitespace, comments and newlines (inside arrays and inline tables).</summary>
    private void SkipWhitespaceCommentsAndNewlines()
    {
        while (!Eof)
        {
            var c = Cur;
            if (c == ' ' || c == '\t' || c == '\n')
            {
                _pos++;
            }
            else if (c == '\r')
            {
                ExpectNewline();
            }
            else if (c == '#')
            {
                SkipComment();
            }
            else
            {
                return;
            }
        }
    }

    private void Enter()
    {
        if (++_depth > MaxNesting)
        {
            throw Error($"Arrays and inline tables are nested more than {MaxNesting} levels deep.");
        }
    }

    // ---- Keys and tables -------------------------------------------------------------------

    private List<TomlKey> ParseKey()
    {
        var keys = new List<TomlKey>();
        while (true)
        {
            keys.Add(ParseSimpleKey());
            SkipWhitespace();
            if (Eof || Cur != '.')
            {
                return keys;
            }

            _pos++;
            SkipWhitespace();
        }
    }

    private TomlKey ParseSimpleKey()
    {
        var start = _pos;
        var position = PositionAt(start);
        if (Eof)
        {
            throw Error("Expected a key, but found end of file.");
        }

        var c = Cur;
        if (c == '"' || c == '\'')
        {
            if (At(_pos + 1) == c && At(_pos + 2) == c)
            {
                throw Error("Multi-line strings cannot be used as keys.");
            }

            var name = c == '"' ? ParseBasicString() : ParseLiteralString();
            return new TomlKey(name, position);
        }

        while (!Eof && IsBareKeyChar(Cur))
        {
            _pos++;
        }

        if (_pos == start)
        {
            throw Error($"Expected a key, but found {Describe(_pos)}.");
        }

        return new TomlKey(_s[start.._pos], position);
    }

    private static string FormatKey(List<TomlKey> keys, int count)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append('.');
            }

            var name = keys[i].Name;
            if (name.Length > 0 && name.All(IsBareKeyChar))
            {
                builder.Append(name);
            }
            else
            {
                builder.Append('"');
                foreach (var ch in name)
                {
                    if (ch is '"' or '\\')
                    {
                        builder.Append('\\');
                    }

                    builder.Append(ch);
                }

                builder.Append('"');
            }
        }

        return builder.ToString();
    }

    private static string DescribeNode(TomlNode node) => node switch
    {
        TomlTable { State: TableState.Inline } => "an inline table",
        TomlTable => "a table",
        TomlArray { IsArrayOfTables: true } => "an array of tables",
        TomlArray => "an array",
        TomlString => "a string",
        TomlInteger => "an integer",
        TomlFloat => "a float",
        TomlBoolean => "a boolean",
        TomlOffsetDateTime => "an offset date-time",
        TomlLocalDateTime => "a local date-time",
        TomlLocalDate => "a local date",
        _ => "a local time",
    };

    private void ParseHeader()
    {
        var start = _pos;
        var headerPosition = PositionAt(start);
        var isArray = At(_pos + 1) == '[';
        _pos += isArray ? 2 : 1;
        SkipWhitespace();
        var keys = ParseKey();
        SkipWhitespace();
        if (Eof || Cur != ']' || (isArray && At(_pos + 1) != ']'))
        {
            throw Error(isArray
                ? $"Expected ']]' to close the array of tables header, but found {Describe(_pos)}."
                : $"Expected ']' to close the table header, but found {Describe(_pos)}.");
        }

        _pos += isArray ? 2 : 1;

        var table = _root;
        for (var i = 0; i < keys.Count - 1; i++)
        {
            var key = keys[i];
            if (!table.TryGetValue(key.Name, out var node))
            {
                var created = new TomlTable(key.Position, TableState.Implicit);
                table.Add(key, created);
                table = created;
            }
            else if (node is TomlTable { State: not TableState.Inline } existing)
            {
                table = existing;
            }
            else if (node is TomlArray { IsArrayOfTables: true } array)
            {
                table = (TomlTable)array[^1];
            }
            else
            {
                throw new TomlException(
                    $"Cannot define table '{FormatKey(keys, keys.Count)}': '{FormatKey(keys, i + 1)}' is already defined as {DescribeNode(node)} at {node.Position}.",
                    key.Position);
            }
        }

        var last = keys[^1];
        var name = FormatKey(keys, keys.Count);
        table.TryGetValue(last.Name, out var target);
        if (isArray)
        {
            TomlArray array;
            if (target is null)
            {
                array = new TomlArray(headerPosition, isArrayOfTables: true);
                table.Add(last, array);
            }
            else if (target is TomlArray { IsArrayOfTables: true } existing)
            {
                array = existing;
            }
            else
            {
                throw new TomlException(
                    target is TomlArray
                        ? $"Cannot append to array '{name}' with [[{name}]]: it was defined as a static array at {target.Position}."
                        : $"Cannot define array of tables '{name}': it is already defined as {DescribeNode(target)} at {target.Position}.",
                    headerPosition);
            }

            var element = new TomlTable(headerPosition, TableState.ArrayElement);
            array.Add(element);
            _current = element;
        }
        else
        {
            if (target is null)
            {
                var created = new TomlTable(headerPosition, TableState.Header);
                table.Add(last, created);
                _current = created;
            }
            else if (target is TomlTable { State: TableState.Implicit } implicitTable)
            {
                implicitTable.State = TableState.Header;
                implicitTable.Position = headerPosition;
                _current = implicitTable;
            }
            else
            {
                var reason = target switch
                {
                    TomlTable { State: TableState.Dotted } => $"it is already defined by dotted keys at {target.Position}",
                    TomlTable { State: TableState.Header or TableState.ArrayElement } => $"it is already defined at {target.Position}",
                    _ => $"it is already defined as {DescribeNode(target)} at {target.Position}",
                };
                throw new TomlException($"Cannot define table '{name}': {reason}.", headerPosition);
            }
        }
    }

    private void ParseKeyValue(TomlTable table)
    {
        var keys = ParseKey();
        SkipWhitespace();
        if (Eof || Cur != '=')
        {
            throw Error($"Expected '=' after the key '{FormatKey(keys, keys.Count)}', but found {Describe(_pos)}.");
        }

        _pos++;
        SkipWhitespace();
        var value = ParseValue();

        for (var i = 0; i < keys.Count - 1; i++)
        {
            var key = keys[i];
            if (!table.TryGetValue(key.Name, out var node))
            {
                var created = new TomlTable(key.Position, TableState.Dotted);
                table.Add(key, created);
                table = created;
                continue;
            }

            if (node is TomlTable { State: TableState.Dotted } dotted)
            {
                table = dotted;
                continue;
            }

            var prefix = FormatKey(keys, i + 1);
            var reason = node switch
            {
                TomlTable { State: TableState.Inline } =>
                    $"'{prefix}' is an inline table (at {node.Position}), and inline tables cannot be extended",
                TomlTable =>
                    $"the table '{prefix}' is defined by a header (at {node.Position}), so dotted keys cannot add to it here",
                _ => $"'{prefix}' is already defined as {DescribeNode(node)} at {node.Position}",
            };
            throw new TomlException($"Cannot set key '{FormatKey(keys, keys.Count)}': {reason}.", key.Position);
        }

        var last = keys[^1];
        if (table.TryGetKey(last.Name, out var existingKey))
        {
            throw new TomlException(
                $"Duplicate key '{FormatKey(keys, keys.Count)}': it is already defined at {existingKey.Position}.",
                last.Position);
        }

        table.Add(last, value);
    }

    // ---- Values ----------------------------------------------------------------------------

    private TomlNode ParseValue()
    {
        if (Eof)
        {
            throw Error("Expected a value, but found end of file.");
        }

        var start = _pos;
        var position = PositionAt(start);
        var c = Cur;
        switch (c)
        {
            case '"':
                return new TomlString(position,
                    At(_pos + 1) == '"' && At(_pos + 2) == '"' ? ParseMultiLineBasicString() : ParseBasicString());
            case '\'':
                return new TomlString(position,
                    At(_pos + 1) == '\'' && At(_pos + 2) == '\'' ? ParseMultiLineLiteralString() : ParseLiteralString());
            case '[':
                return ParseArray(position);
            case '{':
                return ParseInlineTable(position);
            case 't' when string.CompareOrdinal(_s, _pos, "true", 0, 4) == 0:
                _pos += 4;
                return new TomlBoolean(position, true);
            case 'f' when string.CompareOrdinal(_s, _pos, "false", 0, 5) == 0:
                _pos += 5;
                return new TomlBoolean(position, false);
            case '+' or '-' or 'i' or 'n' or (>= '0' and <= '9'):
                if (IsDigits(_pos, 4) && At(_pos + 4) == '-')
                {
                    return ParseDateOrDateTime(position);
                }

                if (IsDigits(_pos, 2) && At(_pos + 2) == ':')
                {
                    return new TomlLocalTime(position, new TimeOnly(ParseTime()));
                }

                return ParseNumber(position);
            default:
                throw Error($"Expected a value, but found {Describe(_pos)}.");
        }
    }

    private TomlArray ParseArray(TomlPosition position)
    {
        Enter();
        _pos++; // '['
        var array = new TomlArray(position, isArrayOfTables: false);
        while (true)
        {
            SkipWhitespaceCommentsAndNewlines();
            if (!Eof && Cur == ']')
            {
                _pos++;
                break;
            }

            array.Add(ParseValue());
            SkipWhitespaceCommentsAndNewlines();
            if (!Eof && Cur == ',')
            {
                _pos++;
                continue;
            }

            if (!Eof && Cur == ']')
            {
                _pos++;
                break;
            }

            throw Error($"Expected ',' or ']' in the array that starts at {position}, but found {Describe(_pos)}.");
        }

        _depth--;
        return array;
    }

    private TomlTable ParseInlineTable(TomlPosition position)
    {
        Enter();
        _pos++; // '{'
        var table = new TomlTable(position, TableState.Inline);
        while (true)
        {
            SkipWhitespaceCommentsAndNewlines();
            if (!Eof && Cur == '}')
            {
                _pos++;
                break;
            }

            ParseKeyValue(table);
            SkipWhitespaceCommentsAndNewlines();
            if (!Eof && Cur == ',')
            {
                _pos++;
                continue;
            }

            if (!Eof && Cur == '}')
            {
                _pos++;
                break;
            }

            throw Error($"Expected ',' or '}}' in the inline table that starts at {position}, but found {Describe(_pos)}.");
        }

        Seal(table);
        _depth--;
        return table;
    }

    private static void Seal(TomlTable table)
    {
        foreach (var value in table.Values)
        {
            if (value is TomlTable { State: TableState.Dotted } child)
            {
                child.State = TableState.Inline;
                Seal(child);
            }
        }
    }

    // ---- Strings ---------------------------------------------------------------------------

    private string ParseBasicString()
    {
        var start = _pos;
        _pos++; // '"'
        var builder = new StringBuilder();
        while (true)
        {
            if (Eof)
            {
                throw Error(start, "Unterminated string: expected a closing '\"'.");
            }

            var c = Cur;
            if (c == '"')
            {
                _pos++;
                return builder.ToString();
            }

            if (c == '\\')
            {
                ParseEscape(builder, multiLine: false);
            }
            else if (c == '\n' || c == '\r')
            {
                throw Error("Unterminated string: a basic string must end on the line it starts; use '\"\"\"' for a multi-line string.");
            }
            else if (IsControl(c))
            {
                throw Error($"Control character U+{(int)c:X4} must be escaped in strings.");
            }
            else
            {
                builder.Append(c);
                _pos++;
            }
        }
    }

    private string ParseMultiLineBasicString()
    {
        var start = _pos;
        _pos += 3;
        SkipFirstNewline();
        var builder = new StringBuilder();
        while (true)
        {
            if (Eof)
            {
                throw Error(start, "Unterminated multi-line string: expected a closing '\"\"\"'.");
            }

            var c = Cur;
            if (c == '"')
            {
                if (ReadClosingQuotes('"', builder))
                {
                    return builder.ToString();
                }
            }
            else if (c == '\\')
            {
                ParseEscape(builder, multiLine: true);
            }
            else
            {
                AppendMultiLineChar(builder, c, "strings");
            }
        }
    }

    private string ParseLiteralString()
    {
        var start = _pos;
        _pos++; // '\''
        var begin = _pos;
        while (true)
        {
            if (Eof)
            {
                throw Error(start, "Unterminated literal string: expected a closing \"'\".");
            }

            var c = Cur;
            if (c == '\'')
            {
                var value = _s[begin.._pos];
                _pos++;
                return value;
            }

            if (c == '\n' || c == '\r')
            {
                throw Error("Unterminated literal string: a literal string must end on the line it starts; use \"'''\" for a multi-line string.");
            }

            if (IsControl(c))
            {
                throw Error($"Control character U+{(int)c:X4} is not allowed in literal strings.");
            }

            _pos++;
        }
    }

    private string ParseMultiLineLiteralString()
    {
        var start = _pos;
        _pos += 3;
        SkipFirstNewline();
        var builder = new StringBuilder();
        while (true)
        {
            if (Eof)
            {
                throw Error(start, "Unterminated multi-line literal string: expected a closing \"'''\".");
            }

            var c = Cur;
            if (c == '\'')
            {
                if (ReadClosingQuotes('\'', builder))
                {
                    return builder.ToString();
                }
            }
            else
            {
                AppendMultiLineChar(builder, c, "literal strings");
            }
        }
    }

    private void SkipFirstNewline()
    {
        if (!Eof && Cur == '\n')
        {
            _pos++;
        }
        else if (!Eof && Cur == '\r' && At(_pos + 1) == '\n' && !AtEnd(_pos + 1))
        {
            _pos += 2;
        }
    }

    private void AppendMultiLineChar(StringBuilder builder, char c, string what)
    {
        if (c == '\r')
        {
            if (At(_pos + 1) != '\n' || AtEnd(_pos + 1))
            {
                throw Error("A carriage return must be followed by a line feed.");
            }

            builder.Append('\n');
            _pos += 2;
            return;
        }

        if (c != '\n' && IsControl(c))
        {
            throw Error($"Control character U+{(int)c:X4} is not allowed in multi-line {what}.");
        }

        builder.Append(c);
        _pos++;
    }

    /// <summary>
    /// Handles a run of quotes inside a multi-line string. Returns true when the run closes the
    /// string; up to two quotes directly before the closing delimiter belong to the content.
    /// </summary>
    private bool ReadClosingQuotes(char quote, StringBuilder builder)
    {
        var count = 0;
        while (At(_pos + count) == quote && !AtEnd(_pos + count))
        {
            count++;
        }

        if (count < 3)
        {
            builder.Append(quote, count);
            _pos += count;
            return false;
        }

        if (count > 5)
        {
            throw Error(_pos + 5, $"Too many {(quote == '"' ? "quotation marks" : "apostrophes")} in a row: a multi-line string cannot contain three of them.");
        }

        builder.Append(quote, count - 3);
        _pos += count;
        return true;
    }

    private void ParseEscape(StringBuilder builder, bool multiLine)
    {
        var start = _pos;
        _pos++; // '\\'
        if (Eof)
        {
            throw Error(start, "Unterminated escape sequence.");
        }

        var c = Cur;
        _pos++;
        switch (c)
        {
            case 'b': builder.Append('\b'); return;
            case 't': builder.Append('\t'); return;
            case 'n': builder.Append('\n'); return;
            case 'f': builder.Append('\f'); return;
            case 'r': builder.Append('\r'); return;
            case 'e': builder.Append('\u001B'); return;
            case '"': builder.Append('"'); return;
            case '\\': builder.Append('\\'); return;
            case 'x': AppendCodePoint(builder, start, 2); return;
            case 'u': AppendCodePoint(builder, start, 4); return;
            case 'U': AppendCodePoint(builder, start, 8); return;
        }

        if (multiLine && (c == ' ' || c == '\t' || c == '\n' || c == '\r'))
        {
            // Line-ending backslash: only whitespace may follow it on the line.
            _pos--;
            SkipWhitespace();
            if (Eof || (Cur != '\n' && Cur != '\r'))
            {
                throw Error(start, "Invalid escape sequence: a line-ending backslash must be followed only by whitespace until the end of the line.");
            }

            while (!Eof && (Cur == ' ' || Cur == '\t' || Cur == '\n' || Cur == '\r'))
            {
                if (Cur == '\r')
                {
                    ExpectNewline();
                }
                else
                {
                    _pos++;
                }
            }

            return;
        }

        throw Error(start, $"Invalid escape sequence '\\{(IsControl(c) || c == '\n' ? $"U+{(int)c:X4}" : c.ToString())}'.");
    }

    private void AppendCodePoint(StringBuilder builder, int start, int digits)
    {
        var value = 0;
        for (var i = 0; i < digits; i++)
        {
            var digit = Eof ? -1 : HexValue(Cur);
            if (digit < 0)
            {
                throw Error(start, $"Invalid escape sequence: '\\{_s[start + 1]}' must be followed by {digits} hexadecimal digits.");
            }

            value = (value * 16) + digit;
            _pos++;
        }

        if (!Rune.IsValid(value))
        {
            throw Error(start, $"Invalid escape sequence '{_s[start.._pos]}': U+{value:X} is not a Unicode scalar value.");
        }

        builder.Append(new Rune(value).ToString());
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    // ---- Numbers ---------------------------------------------------------------------------

    private TomlNode ParseNumber(TomlPosition position)
    {
        var start = _pos;
        var negative = false;
        var signed = false;
        if (Cur is '+' or '-')
        {
            negative = Cur == '-';
            signed = true;
            _pos++;
        }

        if (string.CompareOrdinal(_s, _pos, "inf", 0, 3) == 0)
        {
            _pos += 3;
            return new TomlFloat(position, negative ? double.NegativeInfinity : double.PositiveInfinity);
        }

        if (string.CompareOrdinal(_s, _pos, "nan", 0, 3) == 0)
        {
            _pos += 3;
            return new TomlFloat(position, negative ? -double.NaN : double.NaN);
        }

        var bodyStart = _pos;
        while (!Eof)
        {
            var c = Cur;
            if (IsBareKeyChar(c) && c != '-')
            {
                _pos++;
            }
            else if (c == '.')
            {
                _pos++;
            }
            else if ((c == '+' || c == '-') && At(_pos - 1) is 'e' or 'E' && _pos > bodyStart)
            {
                _pos++;
            }
            else
            {
                break;
            }
        }

        var body = _s[bodyStart.._pos];
        var text = _s[start.._pos];
        if (body.Length == 0)
        {
            throw Error(start, $"Expected a value, but found {Describe(start)}.");
        }

        if (body.Length > 1 && body[0] == '0' && body[1] is 'x' or 'o' or 'b')
        {
            if (signed)
            {
                throw new TomlException($"Invalid integer '{text}': hexadecimal, octal and binary integers cannot have a sign.", position);
            }

            var radix = body[1] switch { 'x' => 16, 'o' => 8, _ => 2 };
            return new TomlInteger(position, ParseRadixInteger(body[2..], radix, text, position));
        }

        var i = 0;
        if (!ReadDigitRun(body, ref i))
        {
            throw new TomlException($"Invalid number '{text}'.", position);
        }

        if (i > 1 && body[0] == '0')
        {
            throw new TomlException($"Invalid number '{text}': leading zeros are not allowed.", position);
        }

        var isFloat = false;
        if (i < body.Length && body[i] == '.')
        {
            i++;
            isFloat = true;
            if (!ReadDigitRun(body, ref i))
            {
                throw new TomlException($"Invalid float '{text}': a decimal point must be followed by digits.", position);
            }
        }

        if (i < body.Length && body[i] is 'e' or 'E')
        {
            i++;
            isFloat = true;
            if (i < body.Length && body[i] is '+' or '-')
            {
                i++;
            }

            if (!ReadDigitRun(body, ref i))
            {
                throw new TomlException($"Invalid float '{text}': the exponent must have digits.", position);
            }
        }

        if (i != body.Length)
        {
            throw new TomlException($"Invalid number '{text}'.", position);
        }

        var clean = text.Replace("_", string.Empty, StringComparison.Ordinal);
        if (isFloat)
        {
            var value = double.Parse(clean, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (double.IsInfinity(value))
            {
                throw new TomlException($"Float '{text}' is too large for a 64-bit floating-point number.", position);
            }

            return new TomlFloat(position, value);
        }

        if (!long.TryParse(clean, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            throw new TomlException($"Integer '{text}' is out of the range of a 64-bit signed integer.", position);
        }

        return new TomlInteger(position, integer);
    }

    /// <summary>Reads digits separated by single underscores; returns false if the run is malformed.</summary>
    private static bool ReadDigitRun(string body, ref int i)
    {
        var start = i;
        while (i < body.Length)
        {
            if (IsDigit(body[i]))
            {
                i++;
            }
            else if (body[i] == '_' && i > start && i + 1 < body.Length && IsDigit(body[i + 1]))
            {
                i++;
            }
            else
            {
                break;
            }
        }

        return i > start && body[i - 1] != '_' && !(i < body.Length && body[i] == '_');
    }

    private static long ParseRadixInteger(string digits, int radix, string text, TomlPosition position)
    {
        if (digits.Length == 0 || digits[0] == '_' || digits[^1] == '_')
        {
            throw new TomlException($"Invalid integer '{text}': expected digits after the prefix, with underscores only between digits.", position);
        }

        ulong value = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var c = digits[i];
            if (c == '_')
            {
                if (digits[i - 1] == '_')
                {
                    throw new TomlException($"Invalid integer '{text}': underscores must be between digits.", position);
                }

                continue;
            }

            var digit = HexValue(c);
            if (digit < 0 || digit >= radix)
            {
                throw new TomlException($"Invalid integer '{text}': '{c}' is not a base-{radix} digit.", position);
            }

            if (value > (long.MaxValue - (ulong)digit) / (ulong)radix)
            {
                throw new TomlException($"Integer '{text}' is out of the range of a 64-bit signed integer.", position);
            }

            value = (value * (ulong)radix) + (ulong)digit;
        }

        return (long)value;
    }

    // ---- Dates and times -------------------------------------------------------------------

    private int ReadTwoDigits(string what)
    {
        if (!IsDigits(_pos, 2))
        {
            throw Error($"Invalid {what}: expected two digits, but found {Describe(IsDigit(At(_pos)) ? _pos + 1 : _pos)}.");
        }

        var value = ((_s[_pos] - '0') * 10) + (_s[_pos + 1] - '0');
        _pos += 2;
        return value;
    }

    private void Expect(char c, string what)
    {
        if (Eof || Cur != c)
        {
            throw Error($"Invalid {what}: expected '{c}', but found {Describe(_pos)}.");
        }

        _pos++;
    }

    private TomlNode ParseDateOrDateTime(TomlPosition position)
    {
        var start = _pos;
        var year = int.Parse(_s.AsSpan(_pos, 4), CultureInfo.InvariantCulture);
        _pos += 4;
        Expect('-', "date");
        var month = ReadTwoDigits("date month");
        Expect('-', "date");
        var day = ReadTwoDigits("date day");
        if (year < 1 || month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            throw Error(start, $"Invalid date '{_s[start.._pos]}': no such day.");
        }

        var date = new DateOnly(year, month, day);
        var hasTime = !Eof && (Cur is 'T' or 't' || (Cur == ' ' && IsDigits(_pos + 1, 2) && At(_pos + 3) == ':'));
        if (!hasTime)
        {
            return new TomlLocalDate(position, date);
        }

        _pos++;
        var ticks = ParseTime();
        var dateTime = date.ToDateTime(TimeOnly.MinValue).AddTicks(ticks);

        if (!Eof && Cur is 'Z' or 'z')
        {
            _pos++;
            return new TomlOffsetDateTime(position, dateTime, TimeSpan.Zero);
        }

        if (!Eof && Cur is '+' or '-')
        {
            var negative = Cur == '-';
            _pos++;
            var hours = ReadTwoDigits("offset hour");
            Expect(':', "offset");
            var minutes = ReadTwoDigits("offset minute");
            if (hours > 23 || minutes > 59)
            {
                throw Error(start, $"Invalid offset in '{_s[start.._pos]}': hours must be 00-23 and minutes 00-59.");
            }

            var offset = new TimeSpan(hours, minutes, 0);
            return new TomlOffsetDateTime(position, dateTime, negative ? -offset : offset);
        }

        return new TomlLocalDateTime(position, dateTime);
    }

    /// <summary>Parses <c>HH:MM[:SS[.fraction]]</c> and returns the time of day in ticks.</summary>
    private long ParseTime()
    {
        var start = _pos;
        var hour = ReadTwoDigits("time hour");
        Expect(':', "time");
        var minute = ReadTwoDigits("time minute");
        var second = 0;
        long fraction = 0;
        if (!Eof && Cur == ':')
        {
            _pos++;
            second = ReadTwoDigits("time second");
            if (!Eof && Cur == '.')
            {
                _pos++;
                if (!IsDigits(_pos, 1))
                {
                    throw Error($"Invalid time: expected digits after the decimal point, but found {Describe(_pos)}.");
                }

                // Precision is 100 ns (7 digits); further digits are truncated as the spec requires.
                var digits = 0;
                while (IsDigits(_pos, 1))
                {
                    if (digits < 7)
                    {
                        fraction = (fraction * 10) + (_s[_pos] - '0');
                        digits++;
                    }

                    _pos++;
                }

                for (; digits < 7; digits++)
                {
                    fraction *= 10;
                }
            }
        }

        if (hour > 23 || minute > 59 || second > 59)
        {
            throw Error(start, second == 60 && hour <= 23 && minute <= 59
                ? $"Invalid time '{_s[start.._pos]}': leap seconds are not supported."
                : $"Invalid time '{_s[start.._pos]}': hours must be 00-23, minutes 00-59 and seconds 00-59.");
        }

        return new TimeSpan(hour, minute, second).Ticks + fraction;
    }
}
