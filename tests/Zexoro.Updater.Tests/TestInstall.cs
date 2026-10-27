using System.Net;
using System.Security.Cryptography;
using System.Text;
using Zexoro.Updates;
using Zexoro.Versioning;

namespace Zexoro.Updater.Tests;

/// <summary>A per-user install in a temporary directory, with a fake registry, processes and HTTP server.</summary>
internal sealed class TestInstall : IDisposable
{
    public const string App = "com.example.notes";
    public static readonly Uri FeedUrl = new("https://example.com/updates/stable/feed.json");

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-updater-tests-");

    public TestInstall(string scope = "user", string version = "1.0.0", string main = "Notes.exe")
    {
        Scope = scope;
        Root = Path.Combine(_dir.FullName, "root");
        LocalAppData = Path.Combine(_dir.FullName, "localappdata");
        Temp = Path.Combine(_dir.FullName, "temp");
        Directory.CreateDirectory(Path.Combine(Root, "app", "lib"));
        Directory.CreateDirectory(Temp);
        File.WriteAllText(Path.Combine(Root, "app", main), "app " + version);
        File.WriteAllText(Path.Combine(Root, "app", "lib", "data.txt"), "data " + version);
        File.WriteAllText(Path.Combine(Root, "zexoro-updater.exe"), "updater " + version);
        File.WriteAllText(Path.Combine(Root, "uninstall.exe"), "uninstall " + version);
        File.WriteAllBytes(Path.Combine(Root, UpdateInstallRecord.FileName), Record(version, main));
        Registry.Entries[App] = new UninstallEntry(version, Formats.Nsis.NsisVersionOrder.Key(SemanticVersion.Parse(version)), 1);
        Processes = new FakeProcesses(this);
        Http = new HttpClient(Handler);
    }

    public string Scope { get; }

    public string Root { get; }

    public string LocalAppData { get; }

    public string Temp { get; }

    public UpdateSigningKey Key { get; } = UpdateSigningKey.Create();

    public FakeRegistry Registry { get; } = new();

    public FakeProcesses Processes { get; }

    public FakeHandler Handler { get; } = new();

    public HttpClient Http { get; }

    public IFileMover Mover { get; set; } = FileMover.Instance;

    public StringWriter Stdout { get; } = new();

    public StringWriter Stderr { get; } = new();

    public string Strategy => Scope == "user" ? "replace" : "reinstall";

    public UpdaterState State => new(LocalAppData, App);

    public UpdaterEnvironment Environment(string? executable = null) => new()
    {
        ExecutablePath = executable ?? Path.Combine(Root, "zexoro-updater.exe"),
        ProcessId = 4242,
        LocalAppData = LocalAppData,
        TempDirectory = Temp,
        Processes = Processes,
        Registry = Registry,
        Mover = Mover,
        CreateHttpClient = () => new HttpClient(Handler, disposeHandler: false),
        WaitTimeout = TimeSpan.FromSeconds(1),
    };

    public int Run(params string[] args) => UpdaterApp.Run(args, Stdout, Stderr, Environment());

    public byte[] Record(string version, string main = "Notes.exe", string? app = null) => Encoding.UTF8.GetBytes($$"""
        {
          "schema": 1,
          "app": "{{app ?? App}}",
          "version": "{{version}}",
          "os": "windows", "arch": "x64", "format": "nsis", "scope": "{{Scope}}",
          "channel": "stable",
          "feed": "{{FeedUrl}}",
          "keys": ["{{Key.PublicKey.Text}}"],
          "main": "{{main}}",
          "strategy": "{{Strategy}}",
          "futureField": true
        }
        """);

    /// <summary>Serves a signed feed with one release and its installer bytes.</summary>
    public byte[] Publish(string version, long sequence = 2, byte[]? installer = null, string? minimumUpdater = null)
    {
        installer ??= Encoding.UTF8.GetBytes("installer " + version);
        var url = new Uri($"https://example.com/dl/Notes-{version}-win-x64.exe");
        var artifact = new FeedArtifact(new UpdateTarget("windows", "x64", "nsis", Scope), url, installer.Length, Convert.ToHexStringLower(SHA256.HashData(installer)));
        var release = new FeedRelease(SemanticVersion.Parse(version), [artifact], $"https://example.com/notes/{version}", minimumUpdater is null ? null : SemanticVersion.Parse(minimumUpdater));
        var feed = new UpdateFeed(App, "stable", sequence, DateTimeOffset.UnixEpoch, [release]).ToJson();
        Handler.Responses[FeedUrl.AbsoluteUri] = feed;
        Handler.Responses[FeedUrl.AbsoluteUri + ".sig"] = FeedSignature.Sign(feed, Key);
        Handler.Responses[url.AbsoluteUri] = installer;
        return installer;
    }

    /// <summary>Puts a verified installer into the downloads directory, as <c>download</c> would.</summary>
    public string Stage(string version, byte[]? installer = null)
    {
        installer ??= Encoding.UTF8.GetBytes("installer " + version);
        var state = State;
        Directory.CreateDirectory(state.Downloads);
        var file = $"Notes-{version}-win-x64.exe";
        File.WriteAllBytes(Path.Combine(state.Downloads, file), installer);
        state.WriteStaged(new StagedUpdate(App, "stable", SemanticVersion.Parse(version), null, file, installer.Length,
            Convert.ToHexStringLower(SHA256.HashData(installer)), new Uri($"https://example.com/dl/{file}")));
        return Path.Combine(state.Downloads, file);
    }

    /// <summary>What the stage-mode installer (<c>/S /ZEXORO_STAGE</c>) writes next to the install.</summary>
    public void WriteStagedInstall(string version, string main = "Notes.exe")
    {
        Directory.CreateDirectory(Path.Combine(Root, "app.new", "lib"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Root, "app.new", main))!);
        File.WriteAllText(Path.Combine(Root, "app.new", main), "app " + version + new string('x', 3000));
        File.WriteAllText(Path.Combine(Root, "app.new", "lib", "data.txt"), "data " + version);
        File.WriteAllText(Path.Combine(Root, "zexoro-updater.exe.new"), "updater " + version);
        File.WriteAllText(Path.Combine(Root, "uninstall.exe.new"), "uninstall " + version);
        File.WriteAllBytes(Path.Combine(Root, UpdateInstallRecord.FileName + ".new"), Record(version, main));
    }

    /// <summary>Every file under the root with its content, for comparing whole install states.</summary>
    public SortedDictionary<string, string> Snapshot()
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            files[Path.GetRelativePath(Root, file).Replace('\\', '/')] = File.ReadAllText(file);
        }

        return files;
    }

    public void Dispose()
    {
        Http.Dispose();
        Handler.Dispose();
        Key.Dispose();
        _dir.Delete(recursive: true);
    }
}

internal sealed class FakeRegistry : IUninstallRegistry
{
    public Dictionary<string, UninstallEntry> Entries { get; } = new(StringComparer.Ordinal);

    /// <summary>Thrown by the next writes, as many as <see cref="FailWrites"/> says.</summary>
    public int FailWrites { get; set; }

    public List<UninstallEntry> Writes { get; } = [];

    public UninstallEntry? Read(UpdateInstallRecord record) => Entries.GetValueOrDefault(record.App);

    public void Write(UpdateInstallRecord record, UninstallEntry entry)
    {
        if (FailWrites > 0)
        {
            FailWrites--;
            throw new UnauthorizedAccessException("registry write denied");
        }

        Writes.Add(entry);
        Entries[record.App] = entry;
    }
}

/// <summary>
/// Runs the temporary updater copy in-process, and the installer through <see cref="Installer"/>
/// (stage mode) or <see cref="ElevatedInstaller"/> (ShellExecute).
/// </summary>
internal sealed class FakeProcesses(TestInstall install) : IProcessHost
{
    public List<string> Log { get; } = [];

    public Func<IReadOnlyList<string>, int> Installer { get; set; } = _ => 0;

    public Func<string, int?> ElevatedInstaller { get; set; } = _ => 0;

    public HashSet<int> Running { get; } = [];

    public List<(string Path, IReadOnlyList<string> Arguments)> Started { get; } = [];

    public bool WaitForExit(int processId, TimeSpan timeout)
    {
        Log.Add($"wait {processId}");
        return !Running.Contains(processId);
    }

    /// <summary>The environment the last installer run got.</summary>
    public IReadOnlyDictionary<string, string>? InstallerEnvironment { get; private set; }

    public int RunAndWait(string path, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string>? environment = null)
    {
        Log.Add($"run {Path.GetFileName(path)} {string.Join(' ', arguments)}");
        if (Path.GetFileName(path) == "zexoro-updater.exe")
        {
            Assert.StartsWith(install.Temp, path, StringComparison.Ordinal);
            return UpdaterApp.Run(arguments, install.Stdout, install.Stderr, install.Environment(path));
        }

        // The staged installer must still be held open with read-only sharing (enforced on Windows).
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<IOException>(() => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite).Dispose());
        }

        InstallerEnvironment = environment;
        return Installer(arguments);
    }

    public int? ShellExecuteAndWait(string path, string arguments, string workingDirectory)
    {
        Log.Add($"shell {Path.GetFileName(path)} {arguments}");
        return ElevatedInstaller(arguments);
    }

    public void Start(string path, IReadOnlyList<string> arguments, string workingDirectory)
    {
        Log.Add($"start {Path.GetFileName(path)} {string.Join(' ', arguments)}".TrimEnd());
        Started.Add((path, arguments));
    }
}

/// <summary>Serves fixed responses by absolute URL; anything else is a 404.</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    public Dictionary<string, byte[]> Responses { get; } = new(StringComparer.Ordinal);

    public bool Offline { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Offline)
        {
            throw new HttpRequestException("no route to host");
        }

        var response = Responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}
