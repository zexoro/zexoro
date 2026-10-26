namespace Zexoro.Updater;

/// <summary>Everything the updater takes from its surroundings, injectable for tests.</summary>
internal sealed record UpdaterEnvironment
{
    /// <summary>This updater's executable.</summary>
    public required string ExecutablePath { get; init; }

    public required int ProcessId { get; init; }

    /// <summary><c>%LOCALAPPDATA%</c>, under which the per-user state lives.</summary>
    public required string LocalAppData { get; init; }

    /// <summary><c>%TEMP%</c>, where apply copies the updater before running it.</summary>
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

    public static UpdaterEnvironment System()
    {
        var path = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "zexoro-updater.exe");
        return new UpdaterEnvironment
        {
            ExecutablePath = path,
            ProcessId = Environment.ProcessId,
            LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
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

internal static class UpdaterVersion
{
    /// <summary>This updater's version, compared with a release's <c>minimumUpdater</c>.</summary>
    public static Versioning.SemanticVersion Current { get; } =
        Versioning.SemanticVersion.TryParse(ProductInfo.Version, out var version) ? version : Versioning.SemanticVersion.Parse("0.0.0");
}
