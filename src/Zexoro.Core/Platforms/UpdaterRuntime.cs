namespace Zexoro.Platforms;

/// <summary>
/// One build of zexoro-updater: a .NET runtime identifier the updater is published for with Native
/// AOT (docs/design/0006-desktop-formats.md section 2, "New updater work"). Every CLI embeds all of
/// them (<c>eng/Updater.targets</c>), so a package for any target can be built on any host.
/// </summary>
/// <param name="Rid">The runtime identifier, e.g. <c>linux-musl-arm64</c>.</param>
/// <param name="Platform">The operating system and architecture it runs on.</param>
/// <param name="Musl">Linux only: linked against musl (Alpine) instead of glibc.</param>
public sealed record UpdaterRuntime(string Rid, TargetPlatform Platform, bool Musl = false)
{
    /// <summary>Points zexoro at a directory of <see cref="DirectoryFileName"/> files, overriding the embedded updaters.</summary>
    public const string DirectoryVariable = "ZEXORO_UPDATER_DIR";

    public static UpdaterRuntime WinX86 { get; } = new("win-x86", new(OperatingSystemFamily.Windows, Architecture.X86));

    public static UpdaterRuntime WinX64 { get; } = new("win-x64", new(OperatingSystemFamily.Windows, Architecture.X64));

    public static UpdaterRuntime WinArm64 { get; } = new("win-arm64", new(OperatingSystemFamily.Windows, Architecture.Arm64));

    public static UpdaterRuntime LinuxX64 { get; } = new("linux-x64", new(OperatingSystemFamily.Linux, Architecture.X64));

    public static UpdaterRuntime LinuxArm64 { get; } = new("linux-arm64", new(OperatingSystemFamily.Linux, Architecture.Arm64));

    public static UpdaterRuntime LinuxMuslX64 { get; } = new("linux-musl-x64", new(OperatingSystemFamily.Linux, Architecture.X64), Musl: true);

    public static UpdaterRuntime LinuxMuslArm64 { get; } = new("linux-musl-arm64", new(OperatingSystemFamily.Linux, Architecture.Arm64), Musl: true);

    public static UpdaterRuntime OsxX64 { get; } = new("osx-x64", new(OperatingSystemFamily.MacOS, Architecture.X64));

    public static UpdaterRuntime OsxArm64 { get; } = new("osx-arm64", new(OperatingSystemFamily.MacOS, Architecture.Arm64));

    /// <summary>Every build, in the order <c>doctor</c> reports them (the same list as <c>eng/Updater.targets</c>).</summary>
    public static IReadOnlyList<UpdaterRuntime> All { get; } =
        [WinX86, WinX64, WinArm64, LinuxX64, LinuxArm64, LinuxMuslX64, LinuxMuslArm64, OsxX64, OsxArm64];

    /// <summary>The file name inside an install: <c>zexoro-updater.exe</c> on Windows, <c>zexoro-updater</c> elsewhere.</summary>
    public string FileName => Platform.OperatingSystem == OperatingSystemFamily.Windows ? "zexoro-updater.exe" : "zexoro-updater";

    /// <summary>
    /// The file name in a directory of updaters (<see cref="DirectoryVariable"/>, the build's
    /// <c>ZexoroUpdaterDir</c>): <c>zexoro-updater-&lt;rid&gt;</c>, plus <c>.exe</c> on Windows.
    /// </summary>
    public string DirectoryFileName => Platform.OperatingSystem == OperatingSystemFamily.Windows ? $"zexoro-updater-{Rid}.exe" : $"zexoro-updater-{Rid}";

    /// <summary>Returns the build for <paramref name="platform"/> (and libc on Linux), or null when there is none.</summary>
    public static UpdaterRuntime? Find(TargetPlatform platform, bool musl = false) =>
        All.FirstOrDefault(r => r.Platform == platform && r.Musl == musl);

    /// <summary>Returns the build with runtime identifier <paramref name="rid"/>, or null.</summary>
    public static UpdaterRuntime? FindRid(string rid) =>
        All.FirstOrDefault(r => string.Equals(r.Rid, rid, StringComparison.Ordinal));

    public override string ToString() => Rid;
}
