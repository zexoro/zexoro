using Zexoro.Configuration;
using Zexoro.Platforms;
using Zexoro.Versioning;

namespace Zexoro.Formats.Nsis;

/// <summary>Everything the generated script depends on; the staging directory holds the files it names.</summary>
public sealed record NsisInstallerSpec
{
    /// <summary>File names inside the staging directory, next to the script.</summary>
    public const string AppDirectoryName = "app";
    public const string IconFileName = "icon.ico";
    public const string InstallRecordFileName = "zexoro-install.json";
    public const string UninstallerFileName = "uninstall.exe";
    public const string OutputFileName = "package.exe";

    public required string AppId { get; init; }

    public required string Name { get; init; }

    public required SemanticVersion Version { get; init; }

    public required string Publisher { get; init; }

    public string? Description { get; init; }

    public Uri? Url { get; init; }

    /// <summary>The main executable, relative to the application directory, with forward slashes.</summary>
    public required string MainExecutable { get; init; }

    /// <summary>The application's architecture (x86, x64 or arm64), from the main executable.</summary>
    public required Architecture Architecture { get; init; }

    public required WindowsInstallScope Scope { get; init; }

    public required WindowsShortcuts Shortcuts { get; init; }

    /// <summary>The staged license file name (<c>license.txt</c> or <c>license.rtf</c>), or null for no license page.</summary>
    public string? LicenseFileName { get; init; }

    /// <summary><c>[windows.uninstall] remove</c>, as written in the configuration.</summary>
    public IReadOnlyList<string> UninstallRemove { get; init; } = [];

    /// <summary>The installed size in KiB, for the uninstall entry.</summary>
    public long EstimatedSizeKb { get; init; }

    /// <summary>Empty directories in the application directory (relative, forward slashes), which <c>File /r</c> skips.</summary>
    public IReadOnlyList<string> EmptyDirectories { get; init; } = [];

    /// <summary>
    /// The command makensis runs to sign the uninstaller and the installer, with <c>%1</c> for the
    /// file, or null to build unsigned. It must not contain backquotes, <c>${</c> or <c>$%</c>.
    /// </summary>
    public string? SignCommand { get; init; }

    /// <summary>The install strategy recorded for the update contract: replace (per-user) or reinstall (per-machine).</summary>
    public string Strategy => Scope == WindowsInstallScope.User ? "replace" : "reinstall";

    public string ScopeName => Scope == WindowsInstallScope.User ? "user" : "machine";

    public bool Is64Bit => Architecture == Architecture.X64 || Architecture == Architecture.Arm64;
}
