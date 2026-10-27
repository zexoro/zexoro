namespace Zexoro.Platforms;

/// <summary>
/// A package format for one operating system, such as <c>nsis</c> for Windows or <c>deb</c> for
/// Linux. The name alone is not unique: <c>zip</c> exists for Windows (a portable folder) and macOS
/// (an <c>.app</c> bundle), which are different formats.
/// </summary>
/// <remarks>
/// This is the catalog of formats zexoro recognizes (docs/design/0006-desktop-formats.md); whether
/// one can be packed yet is up to the packagers.
/// </remarks>
public readonly record struct PackageFormat(OperatingSystemFamily OperatingSystem, string Name)
{
    public static PackageFormat Nsis { get; } = new(OperatingSystemFamily.Windows, "nsis");

    public static PackageFormat WindowsZip { get; } = new(OperatingSystemFamily.Windows, "zip");

    public static PackageFormat Msi { get; } = new(OperatingSystemFamily.Windows, "msi");

    public static PackageFormat TarGz { get; } = new(OperatingSystemFamily.Linux, "tar.gz");

    public static PackageFormat AppImage { get; } = new(OperatingSystemFamily.Linux, "appimage");

    public static PackageFormat Deb { get; } = new(OperatingSystemFamily.Linux, "deb");

    public static PackageFormat Rpm { get; } = new(OperatingSystemFamily.Linux, "rpm");

    public static PackageFormat Apk { get; } = new(OperatingSystemFamily.Linux, "apk");

    public static PackageFormat MacOSZip { get; } = new(OperatingSystemFamily.MacOS, "zip");

    public static PackageFormat Dmg { get; } = new(OperatingSystemFamily.MacOS, "dmg");

    public static PackageFormat Pkg { get; } = new(OperatingSystemFamily.MacOS, "pkg");

    /// <summary>Every recognized format, grouped by operating system.</summary>
    public static IReadOnlyList<PackageFormat> All { get; } = [Nsis, WindowsZip, Msi, TarGz, AppImage, Deb, Rpm, Apk, MacOSZip, Dmg, Pkg];

    /// <summary>The formats of <paramref name="operatingSystem"/>; empty for one zexoro does not package for.</summary>
    public static IReadOnlyList<PackageFormat> For(OperatingSystemFamily operatingSystem) =>
        All.Where(f => f.OperatingSystem == operatingSystem).ToList();

    /// <summary>The names of the formats of <paramref name="operatingSystem"/>, in catalog order.</summary>
    public static IReadOnlyList<string> NamesFor(OperatingSystemFamily operatingSystem) =>
        For(operatingSystem).Select(f => f.Name).ToList();

    /// <summary>
    /// The formats built when the platform table does not list any: <c>nsis</c> on Windows,
    /// <c>tar.gz</c> on Linux, <c>zip</c> on macOS.
    /// </summary>
    public static IReadOnlyList<PackageFormat> DefaultFor(OperatingSystemFamily operatingSystem)
    {
        if (operatingSystem == OperatingSystemFamily.Windows)
        {
            return [Nsis];
        }

        if (operatingSystem == OperatingSystemFamily.Linux)
        {
            return [TarGz];
        }

        return operatingSystem == OperatingSystemFamily.MacOS ? [MacOSZip] : [];
    }

    /// <summary>Finds the format named <paramref name="name"/> (exact, lower case) for <paramref name="operatingSystem"/>.</summary>
    public static bool TryFind(OperatingSystemFamily operatingSystem, string? name, out PackageFormat format)
    {
        foreach (var candidate in All)
        {
            if (candidate.OperatingSystem == operatingSystem && string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                format = candidate;
                return true;
            }
        }

        format = default;
        return false;
    }

    /// <summary>The operating systems that have a format named <paramref name="name"/>.</summary>
    public static IReadOnlyList<OperatingSystemFamily> OperatingSystemsWith(string name) =>
        All.Where(f => string.Equals(f.Name, name, StringComparison.Ordinal)).Select(f => f.OperatingSystem).ToList();

    /// <summary>Returns <c>&lt;os&gt; &lt;name&gt;</c>, e.g. <c>windows nsis</c>.</summary>
    public override string ToString() => $"{OperatingSystem.Name} {Name}";
}

/// <summary>The target platforms zexoro packages for.</summary>
public static class PackagingTargets
{
    /// <summary>
    /// Every supported target: Windows x86, x64 and arm64; Linux x64 and arm64; macOS x64, arm64
    /// and universal.
    /// </summary>
    public static IReadOnlyList<TargetPlatform> All { get; } =
    [
        new(OperatingSystemFamily.Windows, Architecture.X86),
        new(OperatingSystemFamily.Windows, Architecture.X64),
        new(OperatingSystemFamily.Windows, Architecture.Arm64),
        new(OperatingSystemFamily.Linux, Architecture.X64),
        new(OperatingSystemFamily.Linux, Architecture.Arm64),
        new(OperatingSystemFamily.MacOS, Architecture.X64),
        new(OperatingSystemFamily.MacOS, Architecture.Arm64),
        new(OperatingSystemFamily.MacOS, Architecture.Universal),
    ];

    /// <summary>Whether zexoro packages for <paramref name="target"/>.</summary>
    public static bool IsSupported(TargetPlatform target) => All.Contains(target);
}
