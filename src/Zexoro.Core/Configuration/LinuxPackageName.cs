namespace Zexoro.Configuration;

/// <summary>
/// The name of deb, rpm and apk packages (docs/design/0002-update-contract.md section 2,
/// 0006-desktop-formats.md section 2): by default the last segment of <c>app.id</c>, lower-cased;
/// <c>[linux] package</c> overrides it.
/// </summary>
/// <remarks>
/// The rule is the intersection of the three formats' rules, so one name works for all of them:
/// Debian's (lower-case letters, digits, <c>+</c>, <c>-</c> and <c>.</c>, at least two characters,
/// starting with a letter or digit) is the strictest; rpm and apk accept every such name.
/// </remarks>
public static class LinuxPackageName
{
    /// <summary>The longest name accepted; long enough for any real package, short enough for every file system.</summary>
    public const int MaxLength = 128;

    /// <summary>The default name: the last segment of <paramref name="appId"/>, lower-cased.</summary>
    public static string Derive(string appId)
    {
        ArgumentNullException.ThrowIfNull(appId);
        return appId[(appId.LastIndexOf('.') + 1)..].ToLowerInvariant();
    }

    /// <summary>Returns why <paramref name="name"/> is not a valid package name, or null when it is.</summary>
    public static string? Validate(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length < 2)
        {
            return "is too short; a package name has at least two characters";
        }

        if (name.Length > MaxLength)
        {
            return $"is longer than {MaxLength} characters";
        }

        if (!char.IsAsciiLetterLower(name[0]) && !char.IsAsciiDigit(name[0]))
        {
            return "must start with a lower-case letter or a digit";
        }

        var bad = name.FirstOrDefault(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '+' or '-' or '.'));
        return bad != default
            ? $"contains '{bad}'; a package name may contain only lower-case letters, digits, '+', '-' and '.'"
            : null;
    }
}
