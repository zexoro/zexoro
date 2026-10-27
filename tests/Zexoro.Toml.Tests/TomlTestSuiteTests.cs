using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Zexoro.Toml.Tests;

/// <summary>
/// Runs the toml-test 2.2.0 conformance suite (https://github.com/toml-lang/toml-test, vendored
/// under <c>toml-test/</c>) for every case listed in its TOML 1.1.0 file list.
/// </summary>
public partial class TomlTestSuiteTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "toml-test");

    private static IEnumerable<string> ListedCases(string kind) =>
        File.ReadAllLines(Path.Combine(Root, "files-toml-1.1.0"))
            .Where(line => line.StartsWith(kind + "/", StringComparison.Ordinal)
                && line.EndsWith(".toml", StringComparison.Ordinal))
            .Select(line => line[..^".toml".Length]);

    public static TheoryData<string> ValidCases() => new(ListedCases("valid"));

    public static TheoryData<string> InvalidCases() => new(ListedCases("invalid"));

    [Fact]
    public void FileList_HasExpectedCaseCounts()
    {
        Assert.Equal(214, ListedCases("valid").Count());
        Assert.Equal(467, ListedCases("invalid").Count());
    }

    [Theory]
    [MemberData(nameof(ValidCases))]
    public void ValidCase_MatchesExpectedJson(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root, name + ".toml"));
        var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, name + ".json")));

        var document = TomlParser.Parse(bytes);

        AssertEquivalent(expected, ToTaggedJson(document), "$");
    }

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void InvalidCase_ThrowsTomlException(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root, name + ".toml"));

        var exception = Assert.Throws<TomlException>(() => TomlParser.Parse(bytes));

        Assert.True(exception.Line >= 1 && exception.Column >= 1);
    }

    // ---- toml-test's tagged JSON form ------------------------------------------------------

    internal static JsonNode ToTaggedJson(TomlNode node)
    {
        switch (node)
        {
            case TomlTable table:
                var obj = new JsonObject();
                foreach (var (key, value) in table)
                {
                    obj[key] = ToTaggedJson(value);
                }

                return obj;
            case TomlArray array:
                var arr = new JsonArray();
                foreach (var item in array)
                {
                    arr.Add(ToTaggedJson(item));
                }

                return arr;
            case TomlString s:
                return Tagged("string", s.Value);
            case TomlInteger i:
                return Tagged("integer", i.Value.ToString(CultureInfo.InvariantCulture));
            case TomlFloat f:
                return Tagged("float", f.Value switch
                {
                    double.PositiveInfinity => "inf",
                    double.NegativeInfinity => "-inf",
                    _ when double.IsNaN(f.Value) => "nan",
                    _ => f.Value.ToString("R", CultureInfo.InvariantCulture),
                });
            case TomlBoolean b:
                return Tagged("bool", b.Value ? "true" : "false");
            case TomlOffsetDateTime odt:
                var sign = odt.Offset < TimeSpan.Zero ? "-" : "+";
                return Tagged("datetime",
                    odt.DateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture)
                    + sign + odt.Offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture));
            case TomlLocalDateTime ldt:
                return Tagged("datetime-local", ldt.Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
            case TomlLocalDate ld:
                return Tagged("date-local", ld.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            case TomlLocalTime lt:
                return Tagged("time-local", lt.Value.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
            default:
                throw new InvalidOperationException($"Unexpected node {node.GetType()}.");
        }
    }

    private static JsonObject Tagged(string type, string value) => new() { ["type"] = type, ["value"] = value };

    // ---- Semantic comparison, following toml-test's rules ----------------------------------

    private static void AssertEquivalent(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected)
        {
            case JsonArray expectedArray:
                var actualArray = Assert.IsType<JsonArray>(actual, exactMatch: false);
                Assert.True(expectedArray.Count == actualArray.Count,
                    $"{path}: expected {expectedArray.Count} array items, got {actualArray.Count}.");
                for (var i = 0; i < expectedArray.Count; i++)
                {
                    AssertEquivalent(expectedArray[i], actualArray[i], $"{path}[{i}]");
                }

                break;
            case JsonObject expectedObject when IsTaggedValue(expectedObject):
                var actualValue = Assert.IsType<JsonObject>(actual, exactMatch: false);
                Assert.True(IsTaggedValue(actualValue), $"{path}: expected a value, got {actual.ToJsonString()}.");
                var type = (string)expectedObject["type"]!;
                Assert.True(type == (string)actualValue["type"]!,
                    $"{path}: expected type {type}, got {actualValue["type"]}.");
                var want = (string)expectedObject["value"]!;
                var have = (string)actualValue["value"]!;
                Assert.True(ValuesEqual(type, want, have), $"{path}: expected {type} '{want}', got '{have}'.");
                break;
            case JsonObject expectedTable:
                var actualTable = Assert.IsType<JsonObject>(actual, exactMatch: false);
                Assert.False(IsTaggedValue(actualTable), $"{path}: expected a table, got {actual.ToJsonString()}.");
                Assert.True(
                    expectedTable.Select(p => p.Key).Order(StringComparer.Ordinal)
                        .SequenceEqual(actualTable.Select(p => p.Key).Order(StringComparer.Ordinal)),
                    $"{path}: expected keys [{string.Join(", ", expectedTable.Select(p => p.Key))}], got [{string.Join(", ", actualTable.Select(p => p.Key))}].");
                foreach (var (key, value) in expectedTable)
                {
                    AssertEquivalent(value, actualTable[key], $"{path}.{key}");
                }

                break;
            default:
                Assert.Fail($"{path}: unexpected expected JSON {expected?.ToJsonString()}.");
                break;
        }
    }

    private static bool IsTaggedValue(JsonObject obj) =>
        obj.Count == 2 && obj["type"] is JsonValue && obj["value"] is JsonValue;

    private static bool ValuesEqual(string type, string want, string have) => type switch
    {
        "float" => FloatsEqual(want, have),
        "bool" => string.Equals(want, have, StringComparison.OrdinalIgnoreCase),
        "datetime" or "datetime-local" or "date-local" or "time-local" => DateTimeTicks(want) == DateTimeTicks(have),
        _ => string.Equals(want, have, StringComparison.Ordinal),
    };

    private static bool FloatsEqual(string want, string have)
    {
        want = want.ToLowerInvariant();
        have = have.ToLowerInvariant();
        if (want.EndsWith("nan", StringComparison.Ordinal) || have.EndsWith("nan", StringComparison.Ordinal))
        {
            return want.TrimStart('+', '-') == have.TrimStart('+', '-');
        }

        return ParseFloat(want).Equals(ParseFloat(have));
    }

    private static double ParseFloat(string text) => text.TrimStart('+') switch
    {
        "inf" => double.PositiveInfinity,
        "-inf" => double.NegativeInfinity,
        var t => double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture),
    };

    [GeneratedRegex(@"^(?:(\d{4})-(\d{2})-(\d{2}))?[Tt ]?(?:(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d+))?)?)?([Zz]|[+-]\d{2}:\d{2})?$")]
    private static partial Regex DateTimePattern();

    /// <summary>The instant (or local value) in 100 ns ticks, offset applied, as toml-test compares them.</summary>
    private static long DateTimeTicks(string text)
    {
        var match = DateTimePattern().Match(text);
        Assert.True(match.Success, $"'{text}' is not a date or time.");
        long ticks = 0;
        if (match.Groups[1].Success)
        {
            ticks = new DateTime(Int(match.Groups[1]), Int(match.Groups[2]), Int(match.Groups[3])).Ticks;
        }

        if (match.Groups[4].Success)
        {
            ticks += new TimeSpan(Int(match.Groups[4]), Int(match.Groups[5]), match.Groups[6].Success ? Int(match.Groups[6]) : 0).Ticks;
        }

        if (match.Groups[7].Success)
        {
            var fraction = match.Groups[7].Value;
            fraction = fraction.Length > 7 ? fraction[..7] : fraction.PadRight(7, '0');
            ticks += long.Parse(fraction, CultureInfo.InvariantCulture);
        }

        var offset = match.Groups[8].Value;
        if (offset.Length > 1)
        {
            var minutes = (int.Parse(offset.AsSpan(1, 2), CultureInfo.InvariantCulture) * 60)
                + int.Parse(offset.AsSpan(4, 2), CultureInfo.InvariantCulture);
            ticks -= (offset[0] == '-' ? -minutes : minutes) * TimeSpan.TicksPerMinute;
        }

        return ticks;
    }

    private static int Int(Group group) => int.Parse(group.Value, CultureInfo.InvariantCulture);
}
