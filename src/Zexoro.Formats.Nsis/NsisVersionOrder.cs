using System.Globalization;
using System.Text;
using Zexoro.Versioning;

namespace Zexoro.Formats.Nsis;

/// <summary>
/// Encodes a version as a string whose ordinal order equals Semantic Versioning precedence, so the
/// installer can compare the installed version with its own using <c>CompareStringOrdinal</c>.
/// The installer stores it in the uninstall key as <see cref="RegistryValueName"/>; anything that
/// changes the installed version (the updater, for the replace strategy) must update it too.
/// </summary>
/// <remarks>
/// Encoding: major, minor and patch as five zero-padded digits each (each is at most 65535, as the
/// Windows version resource requires); then <c>2</c> for a release, or <c>1</c>, the encoded
/// prerelease identifiers and a closing <c>0</c>. A numeric identifier is <c>1</c>, its digit count
/// as three digits and its digits; an alphanumeric identifier is <c>2</c>, the identifier and
/// <c>!</c> (which sorts before every character an identifier may contain). Build metadata is ignored.
/// </remarks>
public static class NsisVersionOrder
{
    /// <summary>The uninstall key value that holds the installed version's order key.</summary>
    public const string RegistryValueName = "ZexoroVersionOrder";

    /// <summary>The largest major, minor or patch number a Windows version resource can hold.</summary>
    public const ulong MaxCoreNumber = ushort.MaxValue;

    /// <summary>The longest key the installer handles (NSIS strings hold 1024 characters).</summary>
    public const int MaxKeyLength = 512;

    /// <summary>Returns the order key of <paramref name="version"/>.</summary>
    /// <exception cref="ArgumentException">A core number exceeds <see cref="MaxCoreNumber"/>, or the key would exceed <see cref="MaxKeyLength"/>.</exception>
    public static string Key(SemanticVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.Major > MaxCoreNumber || version.Minor > MaxCoreNumber || version.Patch > MaxCoreNumber)
        {
            throw new ArgumentException($"Version {version}: major, minor and patch must each be at most {MaxCoreNumber}.", nameof(version));
        }

        var key = new StringBuilder();
        key.Append(CultureInfo.InvariantCulture, $"{version.Major:D5}{version.Minor:D5}{version.Patch:D5}");
        if (!version.IsPrerelease)
        {
            return key.Append('2').ToString();
        }

        key.Append('1');
        foreach (var identifier in version.Prerelease)
        {
            if (identifier.All(char.IsAsciiDigit))
            {
                key.Append('1').Append(CultureInfo.InvariantCulture, $"{identifier.Length:D3}").Append(identifier);
            }
            else
            {
                key.Append('2').Append(identifier).Append('!');
            }
        }

        key.Append('0');
        return key.Length <= MaxKeyLength
            ? key.ToString()
            : throw new ArgumentException($"Version {version}: the prerelease part is too long for the Windows installer.", nameof(version));
    }
}
