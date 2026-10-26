using System.Diagnostics.CodeAnalysis;

namespace Zexoro.Platforms;

/// <summary>
/// A target platform: an operating system family plus a processor architecture, written as a
/// .NET runtime identifier such as <c>win-x64</c>, <c>osx-arm64</c> or <c>linux-x64</c>.
/// </summary>
public readonly record struct TargetPlatform(OperatingSystemFamily OperatingSystem, Architecture Architecture)
{
    /// <summary>The runtime identifier, e.g. <c>win-x64</c>.</summary>
    public string Rid => $"{OperatingSystem.RidName}-{Architecture.Name}";

    /// <summary>
    /// The platform this process runs on.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">The host is not a known platform.</exception>
    public static TargetPlatform Current =>
        TryGetCurrent(out var platform)
            ? platform
            : throw new PlatformNotSupportedException(
                $"The current platform ({System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}) is not a known target platform.");

    /// <summary>The platform this process runs on, if it is a known one.</summary>
    public static bool TryGetCurrent(out TargetPlatform platform)
    {
        if (OperatingSystemFamily.TryGetCurrent(out var os) && Architecture.TryGetCurrent(out var architecture))
        {
            platform = new TargetPlatform(os, architecture);
            return true;
        }

        platform = default;
        return false;
    }

    /// <summary>
    /// Parses a runtime identifier of the form <c>&lt;os&gt;-&lt;arch&gt;</c>, ignoring case. The
    /// operating system may be given by its runtime identifier prefix (<c>win</c>) or its
    /// descriptive name (<c>windows</c>).
    /// </summary>
    /// <exception cref="FormatException">The text is not a known target platform.</exception>
    public static TargetPlatform Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out var platform)
            ? platform
            : throw new FormatException(
                $"Unknown target platform '{text}'. Expected '<os>-<arch>', such as 'win-x64', 'osx-arm64' or 'linux-x64'.");
    }

    /// <summary>
    /// Parses a runtime identifier of the form <c>&lt;os&gt;-&lt;arch&gt;</c>, ignoring case.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out TargetPlatform platform)
    {
        platform = default;
        if (text is null)
        {
            return false;
        }

        var dash = text.LastIndexOf('-');
        if (dash <= 0
            || !OperatingSystemFamily.TryParse(text[..dash], out var os)
            || !Architecture.TryParse(text[(dash + 1)..], out var architecture))
        {
            return false;
        }

        platform = new TargetPlatform(os, architecture);
        return true;
    }

    /// <summary>Returns the runtime identifier, e.g. <c>win-x64</c>.</summary>
    public override string ToString() => Rid;
}
