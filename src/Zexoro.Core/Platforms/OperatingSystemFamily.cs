using System.Diagnostics.CodeAnalysis;

namespace Zexoro.Platforms;

/// <summary>
/// An operating system family that zexoro can target, such as Windows or Linux.
/// </summary>
/// <remarks>
/// The set is open: a new family is one more static instance, and nothing else in the core
/// enumerates the families. Each family has a descriptive <see cref="Name"/> (used in feeds and
/// install records) and the prefix it uses in .NET runtime identifiers (<see cref="RidName"/>).
/// </remarks>
public readonly struct OperatingSystemFamily : IEquatable<OperatingSystemFamily>
{
    private readonly string? _name;
    private readonly string? _ridName;

    private OperatingSystemFamily(string name, string ridName)
    {
        _name = name;
        _ridName = ridName;
    }

    public static OperatingSystemFamily Windows { get; } = new("windows", "win");

    public static OperatingSystemFamily MacOS { get; } = new("macos", "osx");

    public static OperatingSystemFamily Linux { get; } = new("linux", "linux");

    public static OperatingSystemFamily Android { get; } = new("android", "android");

    public static OperatingSystemFamily IOS { get; } = new("ios", "ios");

    /// <summary>Every known family, in a stable order.</summary>
    public static IReadOnlyList<OperatingSystemFamily> All { get; } = [Windows, MacOS, Linux, Android, IOS];

    /// <summary>The descriptive name, e.g. <c>windows</c> or <c>macos</c>.</summary>
    public string Name => _name ?? string.Empty;

    /// <summary>The runtime identifier prefix, e.g. <c>win</c> or <c>osx</c>.</summary>
    public string RidName => _ridName ?? string.Empty;

    /// <summary>
    /// Parses a family from its descriptive name or its runtime identifier prefix, ignoring case.
    /// </summary>
    /// <exception cref="FormatException">The text names no known family.</exception>
    public static OperatingSystemFamily Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out var family)
            ? family
            : throw new FormatException($"Unknown operating system '{text}'.");
    }

    /// <summary>
    /// Parses a family from its descriptive name or its runtime identifier prefix, ignoring case.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out OperatingSystemFamily family)
    {
        if (text is not null)
        {
            foreach (var candidate in All)
            {
                if (string.Equals(text, candidate.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(text, candidate.RidName, StringComparison.OrdinalIgnoreCase))
                {
                    family = candidate;
                    return true;
                }
            }
        }

        family = default;
        return false;
    }

    /// <summary>The family of the operating system this process runs on, if it is a known one.</summary>
    public static bool TryGetCurrent(out OperatingSystemFamily family)
    {
        // Android and iOS also report as Linux and macOS-like systems, so test them first.
        if (OperatingSystem.IsAndroid())
        {
            family = Android;
        }
        else if (OperatingSystem.IsIOS())
        {
            family = IOS;
        }
        else if (OperatingSystem.IsWindows())
        {
            family = Windows;
        }
        else if (OperatingSystem.IsMacOS())
        {
            family = MacOS;
        }
        else if (OperatingSystem.IsLinux())
        {
            family = Linux;
        }
        else
        {
            family = default;
            return false;
        }

        return true;
    }

    public bool Equals(OperatingSystemFamily other) => string.Equals(_name, other._name, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is OperatingSystemFamily other && Equals(other);

    public override int GetHashCode() => _name is null ? 0 : StringComparer.Ordinal.GetHashCode(_name);

    /// <summary>Returns the descriptive <see cref="Name"/>.</summary>
    public override string ToString() => Name;

    public static bool operator ==(OperatingSystemFamily left, OperatingSystemFamily right) => left.Equals(right);

    public static bool operator !=(OperatingSystemFamily left, OperatingSystemFamily right) => !left.Equals(right);
}
