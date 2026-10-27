using System.Text;

namespace Zexoro.Toml.Tests;

public class TomlParserTests
{
    private static TomlException ParseError(string text) =>
        Assert.Throws<TomlException>(() => TomlParser.Parse(text));

    [Fact]
    public void Parse_KeyAndValue_RecordPositions()
    {
        var root = TomlParser.Parse("# comment\n  name = \"notes\"\n[app]\nversion\t=  1\n");

        Assert.True(root.TryGetKey("name", out var nameKey));
        Assert.Equal(new TomlPosition(2, 3), nameKey.Position);
        Assert.Equal(new TomlPosition(2, 10), root["name"].Position);

        var app = Assert.IsType<TomlTable>(root["app"]);
        Assert.Equal(new TomlPosition(3, 1), app.Position);
        Assert.True(app.TryGetKey("version", out var versionKey));
        Assert.Equal(new TomlPosition(4, 1), versionKey.Position);
        Assert.Equal(new TomlPosition(4, 12), app["version"].Position);
    }

    [Fact]
    public void Parse_DottedKey_RecordsEachSegmentPosition()
    {
        var root = TomlParser.Parse("a . \"b\".c = true");

        Assert.True(root.TryGetKey("a", out var a));
        Assert.Equal(new TomlPosition(1, 1), a.Position);
        var tableA = Assert.IsType<TomlTable>(root["a"]);
        Assert.True(tableA.TryGetKey("b", out var b));
        Assert.Equal(new TomlPosition(1, 5), b.Position);
        var tableB = Assert.IsType<TomlTable>(tableA["b"]);
        Assert.True(tableB.TryGetKey("c", out var c));
        Assert.Equal(new TomlPosition(1, 9), c.Position);
        Assert.Equal(new TomlPosition(1, 13), tableB["c"].Position);
    }

    [Fact]
    public void Parse_ColumnsCountCharactersNotUtf16Units()
    {
        // U+1F600 is two UTF-16 code units but one character.
        var root = TomlParser.Parse("\"\U0001F600\" = 2");

        Assert.Equal(new TomlPosition(1, 7), root["\U0001F600"].Position);
        var error = ParseError("s = \"\U0001F600\" x");
        Assert.Equal(new TomlPosition(1, 9), error.Position);
    }

    [Fact]
    public void Parse_ArrayItemsAndInlineTables_RecordPositions()
    {
        var root = TomlParser.Parse("list = [\n  1,\n  { x = 'y' },\n]\n");

        var list = Assert.IsType<TomlArray>(root["list"]);
        Assert.Equal(new TomlPosition(1, 8), list.Position);
        Assert.Equal(new TomlPosition(2, 3), list[0].Position);
        var inline = Assert.IsType<TomlTable>(list[1]);
        Assert.True(inline.IsInline);
        Assert.Equal(new TomlPosition(3, 3), inline.Position);
        Assert.Equal(new TomlPosition(3, 9), inline["x"].Position);
    }

    [Fact]
    public void Parse_ArrayOfTables_RecordsHeaderPositions()
    {
        var root = TomlParser.Parse("[[p]]\nn = 1\n\n[[p]]\nn = 2\n");

        var array = Assert.IsType<TomlArray>(root["p"]);
        Assert.True(array.IsArrayOfTables);
        Assert.Equal(2, array.Count);
        Assert.Equal(new TomlPosition(1, 1), array[0].Position);
        Assert.Equal(new TomlPosition(4, 1), array[1].Position);
    }

    [Fact]
    public void Parse_ImplicitTableDefinedLater_TakesHeaderPosition()
    {
        var root = TomlParser.Parse("[a.b]\n[a]\n");

        Assert.True(root.TryGetKey("a", out var key));
        Assert.Equal(new TomlPosition(1, 2), key.Position);
        Assert.Equal(new TomlPosition(2, 1), root["a"].Position);
    }

    [Fact]
    public void Parse_KeepsDocumentOrder()
    {
        var root = TomlParser.Parse("z = 1\na = 2\nm = 3\n");

        Assert.Equal(["z", "a", "m"], root.Keys);
    }

    [Fact]
    public void Parse_ValueTypes_AreMapped()
    {
        var root = TomlParser.Parse("""
            s = "a\tb\e\x41"
            i = -0
            h = 0xFF
            big = 9_223_372_036_854_775_807
            f = 6.626e-34
            ni = -inf
            n = nan
            b = false
            odt = 1979-05-27T00:32:00.999999999-07:00
            ldt = 1979-05-27 07:32
            ld = 1979-05-27
            lt = 07:32
            """);

        Assert.Equal("a\tb\u001BA", Assert.IsType<TomlString>(root["s"]).Value);
        Assert.Equal(0, Assert.IsType<TomlInteger>(root["i"]).Value);
        Assert.Equal(255, Assert.IsType<TomlInteger>(root["h"]).Value);
        Assert.Equal(long.MaxValue, Assert.IsType<TomlInteger>(root["big"]).Value);
        Assert.Equal(6.626e-34, Assert.IsType<TomlFloat>(root["f"]).Value);
        Assert.Equal(double.NegativeInfinity, Assert.IsType<TomlFloat>(root["ni"]).Value);
        Assert.True(double.IsNaN(Assert.IsType<TomlFloat>(root["n"]).Value));
        Assert.False(Assert.IsType<TomlBoolean>(root["b"]).Value);

        var odt = Assert.IsType<TomlOffsetDateTime>(root["odt"]);
        Assert.Equal(new DateTime(1979, 5, 27, 0, 32, 0).AddTicks(9_999_999), odt.DateTime);
        Assert.Equal(TimeSpan.FromHours(-7), odt.Offset);
        Assert.True(odt.TryGetDateTimeOffset(out var dto));
        Assert.Equal(new DateTime(1979, 5, 27, 7, 32, 0).AddTicks(9_999_999), dto.UtcDateTime);

        Assert.Equal(new DateTime(1979, 5, 27, 7, 32, 0), Assert.IsType<TomlLocalDateTime>(root["ldt"]).Value);
        Assert.Equal(new DateOnly(1979, 5, 27), Assert.IsType<TomlLocalDate>(root["ld"]).Value);
        Assert.Equal(new TimeOnly(7, 32), Assert.IsType<TomlLocalTime>(root["lt"]).Value);
        Assert.Equal(TomlNodeKind.LocalTime, root["lt"].Kind);
    }

    [Fact]
    public void Parse_OffsetOutsideDateTimeOffsetRange_IsKept()
    {
        var root = TomlParser.Parse("t = 2000-01-01T00:00:00+23:59");

        var odt = Assert.IsType<TomlOffsetDateTime>(root["t"]);
        Assert.Equal(new TimeSpan(23, 59, 0), odt.Offset);
        Assert.False(odt.TryGetDateTimeOffset(out _));
    }

    [Fact]
    public void Parse_InlineTableWithNewlinesAndTrailingComma_IsAccepted()
    {
        var root = TomlParser.Parse("t = {\n  a = 1, # one\n  b.c = 2,\n}\n");

        var table = Assert.IsType<TomlTable>(root["t"]);
        Assert.Equal(2, table.Count);
        Assert.True(Assert.IsType<TomlTable>(table["b"]).IsInline);
    }

    [Fact]
    public void Parse_Utf8WithBom_IsAccepted()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("a = 1\r\n")).ToArray();

        var root = TomlParser.Parse(bytes);

        Assert.Equal(1, Assert.IsType<TomlInteger>(root["a"]).Value);
    }

    [Fact]
    public void Parse_InvalidUtf8_ReportsPosition()
    {
        byte[] bytes = [.. Encoding.UTF8.GetBytes("a = 1\nb = \"x"), 0xFF, (byte)'"'];

        var error = Assert.Throws<TomlException>(() => TomlParser.Parse(bytes));

        Assert.Equal(new TomlPosition(2, 7), error.Position);
        Assert.Contains("not valid UTF-8", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a = 1\na = 2", 2, 1, "Duplicate key 'a': it is already defined at line 1, column 1.")]
    [InlineData("a.b = 1\na.b = 2", 2, 3, "Duplicate key 'a.b': it is already defined at line 1, column 3.")]
    [InlineData("[t]\n[t]", 2, 1, "Cannot define table 't': it is already defined at line 1, column 1.")]
    [InlineData("[a]\nb.c = 1\n[a.b]", 3, 1, "Cannot define table 'a.b': it is already defined by dotted keys at line 2, column 1.")]
    [InlineData("a = 1\n[a.b]", 2, 2, "Cannot define table 'a.b': 'a' is already defined as an integer at line 1, column 5.")]
    [InlineData("a = [1]\n[[a]]", 2, 1, "Cannot append to array 'a' with [[a]]: it was defined as a static array at line 1, column 5.")]
    [InlineData("[[a]]\n[a]", 2, 1, "Cannot define table 'a': it is already defined as an array of tables at line 1, column 1.")]
    [InlineData("[a]\n[[a]]", 2, 1, "Cannot define array of tables 'a': it is already defined as a table at line 1, column 1.")]
    [InlineData("t = { a = 1 }\nt.b = 2", 2, 1, "Cannot set key 't.b': 't' is an inline table (at line 1, column 5), and inline tables cannot be extended.")]
    [InlineData("[a.b.c]\n[a]\nb.d = 1", 3, 1, "Cannot set key 'b.d': the table 'b' is defined by a header (at line 1, column 4), so dotted keys cannot add to it here.")]
    [InlineData("t = { a.b = 1 }\n[t.a]", 2, 2, "Cannot define table 't.a': 't' is already defined as an inline table at line 1, column 5.")]
    public void Parse_Redefinition_ReportsPositionAndReason(string text, int line, int column, string reason)
    {
        var error = ParseError(text);

        Assert.Equal(reason, error.Reason);
        Assert.Equal(new TomlPosition(line, column), error.Position);
        Assert.Equal($"{reason} (at line {line}, column {column})", error.Message);
    }

    [Theory]
    [InlineData("a = ", 1, 5, "Expected a value, but found end of file.")]
    [InlineData("a = 1 b = 2", 1, 7, "Expected the end of the line, but found 'b'.")]
    [InlineData("a", 1, 2, "Expected '=' after the key 'a', but found end of file.")]
    [InlineData("= 1", 1, 1, "Expected a key, but found '='.")]
    [InlineData("a = \"abc", 1, 5, "Unterminated string: expected a closing '\"'.")]
    [InlineData("a = \"\\q\"", 1, 6, "Invalid escape sequence '\\q'.")]
    [InlineData("a = \"\\uD800\"", 1, 6, "Invalid escape sequence '\\uD800': U+D800 is not a Unicode scalar value.")]
    [InlineData("a = 012", 1, 5, "Invalid number '012': leading zeros are not allowed.")]
    [InlineData("a = 9223372036854775808", 1, 5, "Integer '9223372036854775808' is out of the range of a 64-bit signed integer.")]
    [InlineData("a = +0x1", 1, 5, "Invalid integer '+0x1': hexadecimal, octal and binary integers cannot have a sign.")]
    [InlineData("a = 1.", 1, 5, "Invalid float '1.': a decimal point must be followed by digits.")]
    [InlineData("a = 2023-02-29", 1, 5, "Invalid date '2023-02-29': no such day.")]
    [InlineData("a = 24:00", 1, 5, "Invalid time '24:00': hours must be 00-23, minutes 00-59 and seconds 00-59.")]
    [InlineData("a = [1 2]", 1, 8, "Expected ',' or ']' in the array that starts at line 1, column 5, but found '2'.")]
    [InlineData("a = 1 # bell \u0007", 1, 14, "Control character U+0007 is not allowed in comments.")]
    [InlineData("a = 1\r", 1, 6, "A carriage return must be followed by a line feed.")]
    [InlineData("\"\"\"k\"\"\" = 1", 1, 1, "Multi-line strings cannot be used as keys.")]
    [InlineData("[a", 1, 3, "Expected ']' to close the table header, but found end of file.")]
    public void Parse_SyntaxError_ReportsPositionAndReason(string text, int line, int column, string reason)
    {
        var error = ParseError(text);

        Assert.Equal(reason, error.Reason);
        Assert.Equal(new TomlPosition(line, column), error.Position);
    }

    [Fact]
    public void Parse_DeepNesting_FailsCleanly()
    {
        var text = "a = " + new string('[', 10_000) + new string(']', 10_000);

        var error = ParseError(text);

        Assert.Contains("nested more than", error.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Table_Indexer_UnknownKey_ThrowsKeyNotFound()
    {
        var root = TomlParser.Parse("a = 1");

        Assert.Throws<KeyNotFoundException>(() => root["b"]);
        Assert.False(root.ContainsKey("b"));
        Assert.Single(root.Entries);
    }
}
