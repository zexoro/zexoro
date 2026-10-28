using Zexoro.Versioning;

namespace Zexoro.Formats.MacOS;

/// <summary>
/// Maps an application's Semantic Version to the bundle's version keys
/// (docs/design/0006-desktop-formats.md, "As implemented (step 9)").
/// </summary>
/// <remarks>
/// Both <c>CFBundleShortVersionString</c> and <c>CFBundleVersion</c> are
/// <c>major.minor.patch</c>: Apple documents both as up to three period-separated integers, and
/// Launch Services compares <c>CFBundleVersion</c> numerically when several copies of an
/// application exist. A pre-release and build metadata cannot be expressed that way and are left
/// out (<c>1.2.3-beta.1</c> becomes <c>1.2.3</c>); the full version stays in the install record,
/// which is what the updater compares.
/// </remarks>
public static class BundleVersion
{
    /// <summary>Returns <c>major.minor.patch</c>.</summary>
    public static string ShortVersion(SemanticVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return $"{version.Major}.{version.Minor}.{version.Patch}";
    }

    /// <summary>The <c>CFBundleVersion</c>: the same as <see cref="ShortVersion"/>.</summary>
    public static string BuildVersion(SemanticVersion version) => ShortVersion(version);
}
