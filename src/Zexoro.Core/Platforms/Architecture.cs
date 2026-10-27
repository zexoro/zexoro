using System.Diagnostics.CodeAnalysis;
using RuntimeArchitecture = System.Runtime.InteropServices.Architecture;
using RuntimeInformation = System.Runtime.InteropServices.RuntimeInformation;

namespace Zexoro.Platforms;

/// <summary>
/// A processor architecture that zexoro can target, named as in .NET runtime identifiers
/// (<c>x64</c>, <c>x86</c>, <c>arm64</c>, <c>arm</c>), plus <c>universal</c> for macOS fat binaries.
/// </summary>
/// <remarks>The set is open: a new architecture is one more static instance.</remarks>
public readonly struct Architecture : IEquatable<Architecture>
{
    private readonly string? _name;

    private Architecture(string name) => _name = name;

    public static Architecture X64 { get; } = new("x64");

    public static Architecture X86 { get; } = new("x86");

    public static Architecture Arm64 { get; } = new("arm64");

    public static Architecture Arm { get; } = new("arm");

    /// <summary>
    /// A macOS universal (fat) binary with x64 and arm64 slices. It is not a .NET runtime
    /// identifier architecture; zexoro uses it for <c>osx-universal</c> targets.
    /// </summary>
    public static Architecture Universal { get; } = new("universal");

    /// <summary>Every known architecture, in a stable order.</summary>
    public static IReadOnlyList<Architecture> All { get; } = [X64, X86, Arm64, Arm, Universal];

    /// <summary>The runtime identifier name, e.g. <c>x64</c>.</summary>
    public string Name => _name ?? string.Empty;

    /// <summary>Parses an architecture from its name, ignoring case.</summary>
    /// <exception cref="FormatException">The text names no known architecture.</exception>
    public static Architecture Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out var architecture)
            ? architecture
            : throw new FormatException($"Unknown architecture '{text}'.");
    }

    /// <summary>Parses an architecture from its name, ignoring case.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out Architecture architecture)
    {
        if (text is not null)
        {
            foreach (var candidate in All)
            {
                if (string.Equals(text, candidate.Name, StringComparison.OrdinalIgnoreCase))
                {
                    architecture = candidate;
                    return true;
                }
            }
        }

        architecture = default;
        return false;
    }

    /// <summary>
    /// The architecture this process runs as, if it is a known one. Under emulation (for example
    /// an x64 process on arm64 macOS) this is the emulated architecture, which is the one the
    /// process can execute.
    /// </summary>
    public static bool TryGetCurrent(out Architecture architecture)
    {
        architecture = RuntimeInformation.ProcessArchitecture switch
        {
            RuntimeArchitecture.X64 => X64,
            RuntimeArchitecture.X86 => X86,
            RuntimeArchitecture.Arm64 => Arm64,
            RuntimeArchitecture.Arm => Arm,
            _ => default,
        };
        return architecture._name is not null;
    }

    public bool Equals(Architecture other) => string.Equals(_name, other._name, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Architecture other && Equals(other);

    public override int GetHashCode() => _name is null ? 0 : StringComparer.Ordinal.GetHashCode(_name);

    public override string ToString() => Name;

    public static bool operator ==(Architecture left, Architecture right) => left.Equals(right);

    public static bool operator !=(Architecture left, Architecture right) => !left.Equals(right);
}
