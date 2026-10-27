namespace Zexoro.Updater;

/// <summary>Everything the updater takes from its surroundings, injectable for tests.</summary>
internal sealed record UpdaterEnvironment
{
    /// <summary>This updater's executable.</summary>
    public required string ExecutablePath { get; init; }

    public required int ProcessId { get; init; }

    /// <summary>
    /// The directory under which the per-user state lives (in <c>zexoro/updater/&lt;app.id&gt;</c>):
    /// <c>%LOCALAPPDATA%</c> on Windows, <c>$XDG_STATE_HOME</c> (default <c>~/.local/state</c>) on
    /// Linux, <c>~/Library/Application Support</c> on macOS. See <see cref="StateBaseFor"/>.
    /// </summary>
    public required string StateBase { get; init; }

    /// <summary><c>%TEMP%</c> or <c>$TMPDIR</c>, where apply copies the updater before running it.</summary>
    public required string TempDirectory { get; init; }

    public required IProcessHost Processes { get; init; }

    public required IUninstallRegistry Registry { get; init; }

    public IFileMover Mover { get; init; } = FileMover.Instance;

    /// <summary>Creates the HTTP client for feeds and downloads.</summary>
    public required Func<HttpClient> CreateHttpClient { get; init; }

    /// <summary>How long apply waits for the application (and the handing-off updater) to exit.</summary>
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The install root: the directory of this updater, unless <c>apply --from-root</c> names it.</summary>
    public string DefaultRoot => Path.GetDirectoryName(ExecutablePath)!;

    /// <summary>
    /// The state base directory of <paramref name="system"/> (docs/design/0006-desktop-formats.md
    /// section 2): <c>%LOCALAPPDATA%</c> on Windows; on Linux <c>$XDG_STATE_HOME</c> when it is an
    /// absolute path (the XDG Base Directory specification says to ignore relative ones), otherwise
    /// <c>~/.local/state</c>; on macOS <c>~/Library/Application Support</c>.
    /// </summary>
    internal static string StateBaseFor(HostSystem system, Func<string, string?> getVariable, string home, string localAppData) => system switch
    {
        HostSystem.Windows => localAppData,
        HostSystem.MacOS => Path.Combine(home, "Library", "Application Support"),
        _ => getVariable("XDG_STATE_HOME") is { Length: > 0 } state && Path.IsPathRooted(state) ? state : Path.Combine(home, ".local", "state"),
    };

    public static UpdaterEnvironment System()
    {
        var path = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "zexoro-updater.exe" : "zexoro-updater");
        var system = OperatingSystem.IsWindows() ? HostSystem.Windows : OperatingSystem.IsMacOS() ? HostSystem.MacOS : HostSystem.Linux;
        return new UpdaterEnvironment
        {
            ExecutablePath = path,
            ProcessId = Environment.ProcessId,
            StateBase = StateBaseFor(
                system,
                Environment.GetEnvironmentVariable,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify)),
            TempDirectory = Path.GetTempPath(),
            Processes = new SystemProcessHost(),
            Registry = OperatingSystem.IsWindows() ? new WindowsUninstallRegistry() : new UnsupportedRegistry(),
            CreateHttpClient = () =>
            {
                var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"zexoro-updater/{UpdaterVersion.Current}");
                return client;
            },
        };
    }
}

/// <summary>The operating systems whose conventions the updater follows.</summary>
internal enum HostSystem
{
    Windows,
    Linux,
    MacOS,
}

internal static class UpdaterVersion
{
    /// <summary>This updater's version, compared with a release's <c>minimumUpdater</c>.</summary>
    public static Versioning.SemanticVersion Current { get; } =
        Versioning.SemanticVersion.TryParse(ProductInfo.Version, out var version) ? version : Versioning.SemanticVersion.Parse("0.0.0");
}
