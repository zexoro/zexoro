using System.Globalization;
using Zexoro.Versioning;

namespace Zexoro.Formats.Linux;

/// <summary>
/// Maps Semantic Versions to apk versions (docs/design/0006-desktop-formats.md, "As implemented
/// (step 8)"), so that apk orders releases as the updater does.
/// </summary>
/// <remarks>
/// apk versions have a fixed grammar (<c>1.2.3</c>, then suffixes such as <c>_alpha1</c>,
/// <c>_beta2</c>, <c>_rc3</c>, then the package release <c>-r0</c>) and order the suffixes
/// <c>_alpha</c> &lt; <c>_beta</c> &lt; <c>_rc</c> &lt; no suffix, as SemVer orders these
/// pre-releases. Only the pre-releases <c>alpha.N</c>, <c>beta.N</c> and <c>rc.N</c> (N a
/// number) map, to <c>_alphaN</c>, <c>_betaN</c> and <c>_rcN</c>; any other pre-release has no apk
/// form that keeps SemVer's order, so it is refused. Build metadata is dropped, as for deb and rpm.
/// The release is always <see cref="Release"/>: zexoro builds each version once.
/// </remarks>
public static class ApkVersion
{
    /// <summary>The package release of every package (<c>pkgrel</c>).</summary>
    public const string Release = "r0";

    private static readonly string[] Suffixes = ["alpha", "beta", "rc"];

    /// <summary>Returns the apk version of <paramref name="version"/> without the release, for example <c>1.2.3_beta1</c>.</summary>
    /// <exception cref="ArgumentException">The pre-release is not <c>alpha.N</c>, <c>beta.N</c> or <c>rc.N</c>.</exception>
    public static string From(SemanticVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var text = string.Create(CultureInfo.InvariantCulture, $"{version.Major}.{version.Minor}.{version.Patch}");
        if (version.Prerelease.Count == 0)
        {
            return text;
        }

        if (version.Prerelease.Count == 2 && Suffixes.Contains(version.Prerelease[0], StringComparer.Ordinal)
            && version.Prerelease[1].Length is > 0 and <= 18 && version.Prerelease[1].All(char.IsAsciiDigit))
        {
            return $"{text}_{version.Prerelease[0]}{version.Prerelease[1]}";
        }

        throw new ArgumentException(
            $"the pre-release '{string.Join('.', version.Prerelease)}' of version {version} has no apk version; "
            + "apk packages support the pre-releases alpha.N, beta.N and rc.N (for example 1.0.0-beta.2, packaged as 1.0.0_beta2)",
            nameof(version));
    }

    /// <summary>The full package version with the release, for example <c>1.2.3_beta1-r0</c>.</summary>
    /// <exception cref="ArgumentException">The version cannot be mapped (<see cref="From"/>).</exception>
    public static string WithRelease(SemanticVersion version) => From(version) + "-" + Release;
}
