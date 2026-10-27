using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Zexoro.Versioning;

/// <summary>
/// A Semantic Versioning 2.0.0 version (<c>MAJOR.MINOR.PATCH[-PRERELEASE][+BUILD]</c>).
/// </summary>
/// <remarks>
/// Comparison (<see cref="CompareTo(SemanticVersion?)"/> and the comparison operators) follows
/// the specification's precedence rules, which ignore build metadata. Equality
/// (<see cref="Equals(SemanticVersion?)"/>, <c>==</c>) is exact and includes build metadata, so
/// <c>1.0.0+a</c> and <c>1.0.0+b</c> have the same precedence but are not equal.
/// </remarks>
public sealed class SemanticVersion : IEquatable<SemanticVersion>, IComparable<SemanticVersion>, IComparable
{
    private SemanticVersion(ulong major, ulong minor, ulong patch, string[] prerelease, string[] build)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
        Build = build;
    }

    public ulong Major { get; }

    public ulong Minor { get; }

    public ulong Patch { get; }

    /// <summary>The dot-separated pre-release identifiers; empty for a release version.</summary>
    public IReadOnlyList<string> Prerelease { get; }

    /// <summary>The dot-separated build metadata identifiers; empty when there is none.</summary>
    public IReadOnlyList<string> Build { get; }

    /// <summary>Whether the version has pre-release identifiers.</summary>
    public bool IsPrerelease => Prerelease.Count > 0;

    /// <summary>Parses a version.</summary>
    /// <exception cref="FormatException">The text is not a Semantic Versioning 2.0.0 version.</exception>
    public static SemanticVersion Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out var version, out var error)
            ? version
            : throw new FormatException($"'{text}' is not a valid version: {error}");
    }

    /// <summary>Parses a version.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out SemanticVersion? version) =>
        TryParse(text, out version, out _);

    /// <summary>Parses a version, explaining what is wrong on failure.</summary>
    internal static bool TryParse(
        [NotNullWhen(true)] string? text,
        [NotNullWhen(true)] out SemanticVersion? version,
        [NotNullWhen(false)] out string? error)
    {
        version = null;
        if (string.IsNullOrEmpty(text))
        {
            error = "expected MAJOR.MINOR.PATCH, such as 1.2.3";
            return false;
        }

        var rest = text.AsSpan();
        string[] build = [];
        var plus = rest.IndexOf('+');
        if (plus >= 0)
        {
            if (!TryParseIdentifiers(rest[(plus + 1)..], "build metadata", numericLeadingZerosAllowed: true, out build, out error))
            {
                return false;
            }

            rest = rest[..plus];
        }

        string[] prerelease = [];
        var dash = rest.IndexOf('-');
        if (dash >= 0)
        {
            if (!TryParseIdentifiers(rest[(dash + 1)..], "pre-release", numericLeadingZerosAllowed: false, out prerelease, out error))
            {
                return false;
            }

            rest = rest[..dash];
        }

        Span<Range> parts = stackalloc Range[4];
        if (rest.Split(parts, '.') != 3)
        {
            error = "expected MAJOR.MINOR.PATCH, such as 1.2.3";
            return false;
        }

        Span<ulong> numbers = stackalloc ulong[3];
        string[] names = ["major", "minor", "patch"];
        for (var i = 0; i < 3; i++)
        {
            var part = rest[parts[i]];
            if (part.IsEmpty || !IsNumeric(part))
            {
                error = $"the {names[i]} version must be a non-negative integer";
                return false;
            }

            if (part.Length > 1 && part[0] == '0')
            {
                error = $"the {names[i]} version must not have leading zeros";
                return false;
            }

            if (!ulong.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                error = $"the {names[i]} version is too large";
                return false;
            }
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], prerelease, build);
        error = null;
        return true;
    }

    /// <summary>
    /// Compares by precedence (specification section 11). Build metadata is ignored, so a result
    /// of 0 does not imply equality.
    /// </summary>
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = Major.CompareTo(other.Major);
        if (result == 0)
        {
            result = Minor.CompareTo(other.Minor);
        }

        if (result == 0)
        {
            result = Patch.CompareTo(other.Patch);
        }

        return result != 0 ? result : ComparePrerelease(Prerelease, other.Prerelease);
    }

    int IComparable.CompareTo(object? obj) => obj switch
    {
        null => 1,
        SemanticVersion other => CompareTo(other),
        _ => throw new ArgumentException("The object is not a SemanticVersion.", nameof(obj)),
    };

    /// <summary>Exact equality, including build metadata.</summary>
    public bool Equals(SemanticVersion? other) =>
        other is not null
        && Major == other.Major
        && Minor == other.Minor
        && Patch == other.Patch
        && Prerelease.SequenceEqual(other.Prerelease, StringComparer.Ordinal)
        && Build.SequenceEqual(other.Build, StringComparer.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as SemanticVersion);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToString());

    /// <summary>Returns the canonical text, e.g. <c>1.2.3-beta.1+build.5</c>.</summary>
    public override string ToString()
    {
        var text = $"{Major}.{Minor}.{Patch}";
        if (Prerelease.Count > 0)
        {
            text += "-" + string.Join('.', Prerelease);
        }

        if (Build.Count > 0)
        {
            text += "+" + string.Join('.', Build);
        }

        return text;
    }

    public static bool operator ==(SemanticVersion? left, SemanticVersion? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(SemanticVersion? left, SemanticVersion? right) => !(left == right);

    public static bool operator <(SemanticVersion? left, SemanticVersion? right) => Compare(left, right) < 0;

    public static bool operator <=(SemanticVersion? left, SemanticVersion? right) => Compare(left, right) <= 0;

    public static bool operator >(SemanticVersion? left, SemanticVersion? right) => Compare(left, right) > 0;

    public static bool operator >=(SemanticVersion? left, SemanticVersion? right) => Compare(left, right) >= 0;

    private static int Compare(SemanticVersion? left, SemanticVersion? right) =>
        left is null ? (right is null ? 0 : -1) : left.CompareTo(right);

    private static int ComparePrerelease(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        // A release version has higher precedence than any of its pre-releases.
        if (left.Count == 0 || right.Count == 0)
        {
            return (left.Count == 0, right.Count == 0) switch
            {
                (true, true) => 0,
                (true, false) => 1,
                _ => -1,
            };
        }

        for (var i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            var result = CompareIdentifier(left[i], right[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return left.Count.CompareTo(right.Count);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = IsNumeric(left);
        var rightNumeric = IsNumeric(right);
        if (leftNumeric && rightNumeric)
        {
            // Numeric identifiers have no leading zeros, so a longer one is larger.
            var byLength = left.Length.CompareTo(right.Length);
            return byLength != 0 ? byLength : Math.Sign(string.CompareOrdinal(left, right));
        }

        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return Math.Sign(string.CompareOrdinal(left, right));
    }

    private static bool TryParseIdentifiers(
        ReadOnlySpan<char> text,
        string what,
        bool numericLeadingZerosAllowed,
        out string[] identifiers,
        [NotNullWhen(false)] out string? error)
    {
        identifiers = text.ToString().Split('.');
        foreach (var identifier in identifiers)
        {
            if (identifier.Length == 0)
            {
                error = $"{what} identifiers must not be empty";
                return false;
            }

            foreach (var c in identifier)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                {
                    error = $"{what} identifiers may contain only ASCII letters, digits and hyphens ('{identifier}')";
                    return false;
                }
            }

            if (!numericLeadingZerosAllowed && identifier.Length > 1 && identifier[0] == '0' && IsNumeric(identifier))
            {
                error = $"numeric {what} identifiers must not have leading zeros ('{identifier}')";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static bool IsNumeric(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
