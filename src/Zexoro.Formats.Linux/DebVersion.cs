using Zexoro.Versioning;

namespace Zexoro.Formats.Linux;

/// <summary>
/// Maps Semantic Versions to Debian versions (docs/design/0006-desktop-formats.md, "As implemented
/// (step 6)"), so that dpkg orders releases as the updater does.
/// </summary>
/// <remarks>
/// <para>
/// <c>1.2.3</c> stays <c>1.2.3</c>; a pre-release becomes <c>~</c> plus its identifiers
/// (<c>1.2.3-beta.1</c> → <c>1.2.3~beta.1</c>), because <c>~</c> sorts before the end of the
/// string in Debian's comparison, as a pre-release sorts before its release in SemVer. Build
/// metadata is dropped: SemVer ignores it for precedence, so <c>1.2.3+ci.5</c> and <c>1.2.3</c> are
/// the same version for the updater and must be for dpkg too. No Debian revision is written; the
/// whole version is the upstream version.
/// </para>
/// <para>
/// A pre-release identifier with <c>-</c> (<c>1.0.0-rc-1</c>) cannot be mapped: Debian allows
/// <c>-</c> in an upstream version only when a revision follows, and a revision would change the
/// order. Such versions are rejected. One ordering difference remains and is accepted: SemVer puts
/// a numeric identifier before an alphanumeric one at the same position (<c>alpha.1</c> &lt;
/// <c>alpha.beta</c>), dpkg the other way round. Pre-releases that use the same scheme throughout
/// (<c>alpha.N</c>, <c>beta.N</c>, <c>rc.N</c>) compare identically.
/// </para>
/// </remarks>
public static class DebVersion
{
    /// <summary>Returns the Debian version of <paramref name="version"/>.</summary>
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
                $"the pre-release identifier '{bad}' of version {version} contains '-', which a Debian version cannot hold without a revision; "
                + "use '.' to separate identifiers (for example 1.0.0-rc.1)",
                nameof(version));
        }

        return text + "~" + string.Join('.', version.Prerelease);
    }
}
