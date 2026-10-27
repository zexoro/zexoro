using Zexoro.Updates;

namespace Zexoro.Updater;

/// <summary>
/// The <c>notify</c> strategy (docs/design/0002-update-contract.md section 7,
/// 0006-desktop-formats.md section 2): a package manager owns the install (deb, rpm, apk), so the
/// updater only reports updates. <c>check</c> works as for every strategy and reports the
/// artifact's <c>url</c>; <c>download</c> and <c>apply</c> exit 7 without touching the install or
/// the updater's state.
/// </summary>
internal static class Notify
{
    public const string Strategy = "notify";

    /// <summary>The package manager that updates an install of <paramref name="format"/>, for messages.</summary>
    public static string PackageManagerFor(string format) => format switch
    {
        "deb" => "apt or dpkg",
        "rpm" => "dnf, zypper or rpm",
        "apk" => "apk",
        _ => "the system's package manager",
    };

    /// <summary>The result of <c>download</c> or <c>apply</c> for a notify install: exit 7, update with the package manager.</summary>
    public static UpdaterResult ManualUpdate(UpdateInstallRecord record, string command)
    {
        var manager = PackageManagerFor(record.Target.Format);
        return UpdaterResult.Fail(
            UpdaterExitCodes.ManualUpdateNeeded,
            $"{record.App} is installed as a {record.Target.Format} package, which {manager} updates; zexoro-updater does not {command} it. "
            + $"Install the new package with {manager} ('check --json' reports its url).",
            record.Version.ToString());
    }
}
