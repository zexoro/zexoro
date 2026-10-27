using Zexoro.Versioning;

namespace Zexoro.Formats.Linux;

/// <summary>
/// Maps Semantic Versions to rpm's Version and Release (docs/design/0006-desktop-formats.md, "As
/// implemented (step 7)"), so that rpm orders releases as the updater does.
/// </summary>
/// <remarks>
/// The mapping is the one of <see cref="DebVersion"/>: <c>1.2.3</c> stays <c>1.2.3</c>, a
/// pre-release becomes <c>~</c> plus its identifiers (<c>1.2.3-beta.1</c> →
/// <c>1.2.3~beta.1</c>; rpm sorts <c>~</c> before the end of the version since 4.10, as SemVer
/// sorts a pre-release before its release), build metadata is dropped. rpm does not allow
/// <c>-</c> in a Version (it separates Version and Release), so pre-release identifiers with
/// <c>-</c> are refused. rpm, like dpkg, sorts a numeric segment after an alphabetic one
/// (<c>alpha.1</c> &gt; <c>alpha.beta</c>, the reverse of SemVer); schemes such as
/// <c>alpha.N</c>, <c>beta.N</c>, <c>rc.N</c> compare identically. The Release is always
/// <see cref="Release"/>: zexoro builds each version once.
/// </remarks>
public static class RpmVersion
{
    /// <summary>The Release of every package.</summary>
    public const string Release = "1";

    /// <summary>Returns the rpm Version of <paramref name="version"/>.</summary>
    /// <exception cref="ArgumentException">A pre-release identifier contains '-'.</exception>
    public static string From(SemanticVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var text = $"{version.Major}.{version.Minor}.{version.Patch}";
        if (version.Prerelease.Count == 0)
        {
            return text;
        }

        if (version.Prerelease.FirstOrDefault(p => p.Contains('-', StringComparison.Ordinal)) is { } bad)
        {
            throw new ArgumentException(
                $"the pre-release identifier '{bad}' of version {version} contains '-', which an rpm Version cannot hold (it separates Version and Release); "
                + "use '.' to separate identifiers (for example 1.0.0-rc.1)",
                nameof(version));
        }

        return text + "~" + string.Join('.', version.Prerelease);
    }
}

/// <summary>Versions in the file names of deb and rpm packages.</summary>
public static class PackageFileVersion
{
    /// <summary>
    /// The version as it appears in artifact file names: <c>~</c> becomes <c>.</c>, because GitHub
    /// renames release assets that way (verified on 2026-10-27), which would break the
    /// <c>{file}</c> URL of a release fragment. The package's own version keeps <c>~</c>.
    /// </summary>
    public static string From(string packageVersion)
    {
        ArgumentNullException.ThrowIfNull(packageVersion);
        return packageVersion.Replace('~', '.');
    }
}
