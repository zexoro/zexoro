using Zexoro.Updates;

namespace Zexoro.Updater;

/// <summary>
/// Where the install record lies relative to the updater (docs/design/0006-desktop-formats.md
/// section 2): next to it, except inside a macOS bundle, where the updater is
/// <c>&lt;name&gt;.app/Contents/MacOS/zexoro-updater</c> and the record
/// <c>&lt;name&gt;.app/Contents/Resources/zexoro-install.json</c> (code signing allows no data in
/// <c>Contents/MacOS</c>).
/// </summary>
internal static class InstallLayout
{
    /// <summary>Returns the record path for an updater in <paramref name="directory"/>.</summary>
    public static string RecordPath(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var contents = Path.GetDirectoryName(full);
        var bundle = contents is null ? null : Path.GetDirectoryName(contents);
        return Path.GetFileName(full) == "MacOS" && Path.GetFileName(contents) == "Contents"
            && bundle is not null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(contents!, "Resources", UpdateInstallRecord.FileName)
            : Path.Combine(full, UpdateInstallRecord.FileName);
    }
}
