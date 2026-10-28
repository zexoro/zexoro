using Zexoro.Configuration;
using Zexoro.Formats.Nsis;
using Zexoro.Platforms;

namespace Zexoro.Formats.MacOS;

/// <summary>What <see cref="MacBundle.Create"/> needs.</summary>
public sealed record MacBundleRequest
{
    public required ZexoroConfig Config { get; init; }

    /// <summary>The target architecture: x64, arm64 or universal.</summary>
    public required Architecture Architecture { get; init; }

    /// <summary>An empty directory the bundle is created in.</summary>
    public required string StagingDirectory { get; init; }

    /// <summary>A directory to leave out of a flat input (the output directory when it lies inside the input), or null.</summary>
    public string? Exclude { get; init; }

    /// <summary>The updater executable (thin or universal), or null without <c>[updates]</c>.</summary>
    public byte[]? Updater { get; init; }

    /// <summary>The record's update settings, or null without <c>[updates]</c>.</summary>
    public NsisUpdates? Updates { get; init; }

    /// <summary>Loads the icns file for a generated bundle; not called for an existing <c>.app</c>.</summary>
    public required Func<byte[]> LoadIcon { get; init; }

    public TextWriter? Log { get; init; }
}

/// <summary>A bundle on disk.</summary>
/// <param name="Path">The <c>&lt;name&gt;.app</c> directory.</param>
/// <param name="MainExecutable">The main executable relative to the bundle, <c>Contents/MacOS/&lt;name&gt;</c>.</param>
/// <param name="Generated">Whether zexoro wrapped a flat directory (false: the input was a <c>.app</c>).</param>
public sealed record MacBundleResult(string Path, string MainExecutable, bool Generated);

/// <summary>
/// Creates the <c>.app</c> bundle of docs/design/0006-desktop-formats.md section 2: from a flat
/// build output, or from an existing bundle given as <c>[input] dir</c>. Both get
/// <c>Contents/MacOS/zexoro-updater</c> (with <c>[updates]</c>) and
/// <c>Contents/Resources/zexoro-install.json</c> (format <c>app</c>, scope <c>user</c>, strategy
/// <c>bundle</c>). Files are copied by content, so extended attributes (quarantine, Finder
/// information) never reach the bundle, which codesign's strict checks would reject.
/// </summary>
/// <remarks>
/// <para>
/// <b>Flat input.</b> codesign treats everything in <c>Contents/MacOS</c> as code and refuses data
/// files there, but applications usually expect their files next to the executable (a .NET
/// application looks for its assemblies in the executable's directory). So
/// <c>Contents/MacOS/</c> mirrors the input's directory tree: Mach-O files (executables, dynamic
/// libraries) are stored there; every other file goes to <c>Contents/Resources/app/</c> at the same
/// relative path, and <c>Contents/MacOS/</c> gets a relative symbolic link to it in its place. The
/// reverse holds too: <c>Contents/Resources/app/</c> has a link to every Mach-O file. Both trees
/// are complete views of the input, so the application finds every file where it was, whether it
/// locates its files from the executable's path or from a resolved path (the .NET host resolves the
/// links of its application assembly, so <c>AppContext.BaseDirectory</c> is
/// <c>Contents/Resources/app/</c>). The main executable must be a Mach-O file in the input's top
/// directory. Symbolic links of the input are kept when their target is relative and
/// stays inside the input; nested bundles (<c>.app</c>, <c>.framework</c>, …) are refused: their
/// layout and signing order are the developer's, so such applications are packed from an
/// existing <c>.app</c>.
/// </para>
/// <para>
/// <b>Existing <c>.app</c>.</b> Its Info.plist (XML or binary) must name the configured bundle
/// identifier, <c>CFBundleShortVersionString</c> must be the application version's
/// <c>major.minor.patch</c>, and <c>input.main</c> must be <c>Contents/MacOS/&lt;CFBundleExecutable&gt;</c>.
/// It is copied as it is (symbolic links included, when relative and inside the bundle; chains of
/// links, as frameworks use, are allowed), renamed to <c>&lt;app.name&gt;.app</c>, and its Info.plist
/// and icon are left unchanged.
/// </para>
/// </remarks>
public static class MacBundle
{
    public const string RecordFormat = "app";
    public const string Scope = "user";
    public const string Strategy = "bundle";
    public const string UpdaterFileName = "zexoro-updater";
    public const string RecordPath = "Contents/Resources/zexoro-install.json";
    public const string IconName = "AppIcon";

    /// <summary>Where a flat input's non-code files go, relative to the bundle.</summary>
    public const string ResourcesAppDirectory = "Contents/Resources/app";

    private const UnixFileMode DirectoryMode = (UnixFileMode)0x1ED;
    private const UnixFileMode ExecutableMode = (UnixFileMode)0x1ED;
    private const UnixFileMode RegularMode = (UnixFileMode)0x1A4;

    /// <summary>The bundle's directory name: <c>&lt;app.name&gt;.app</c>.</summary>
    public static string BundleName(AppConfig app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Name + ".app";
    }

    /// <summary>Whether <paramref name="directory"/> is an application bundle: named <c>*.app</c>.</summary>
    public static bool IsAppDirectory(string directory) =>
        Path.TrimEndingDirectorySeparator(directory).EndsWith(".app", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Info.plist of a generated bundle.</summary>
    public static Dictionary<string, object> CreateInfoPlist(ZexoroConfig config, string executable)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(executable);
        var app = config.App;
        var plist = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["CFBundleDevelopmentRegion"] = "en",
            ["CFBundleDisplayName"] = app.Name,
            ["CFBundleExecutable"] = executable,
            ["CFBundleIconFile"] = IconName,
            ["CFBundleIdentifier"] = config.MacOS.BundleIdentifier(app),
            ["CFBundleInfoDictionaryVersion"] = "6.0",
            ["CFBundleName"] = app.Name,
            ["CFBundlePackageType"] = "APPL",
            ["CFBundleShortVersionString"] = BundleVersion.ShortVersion(app.Version),
            ["CFBundleVersion"] = BundleVersion.BuildVersion(app.Version),
            ["NSHighResolutionCapable"] = true,
            ["NSHumanReadableCopyright"] = app.Publisher,
        };
        if (config.MacOS.MinimumVersion is { } minimum)
        {
            plist["LSMinimumSystemVersion"] = minimum;
        }

        if (config.MacOS.Category is { } category)
        {
            plist["LSApplicationCategoryType"] = category;
        }

        return plist;
    }

    /// <summary>Creates the bundle in <see cref="MacBundleRequest.StagingDirectory"/>.</summary>
    /// <exception cref="MacPackException">The input cannot be made into a bundle.</exception>
    /// <exception cref="IOException">Reading or writing fails.</exception>
    public static MacBundleResult Create(MacBundleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = request.Config;
        var input = Path.GetFullPath(config.Input.Directory.FullPath);
        var bundle = Path.Combine(request.StagingDirectory, BundleName(config.App));
        var result = IsAppDirectory(input) ? CopyExisting(config, input, bundle) : Wrap(request, input, bundle);
        var target = new TargetPlatform(OperatingSystemFamily.MacOS, request.Architecture);
        var record = InstallRecord.Create(config.App.Id, config.App.Version, target, RecordFormat, Scope, Strategy, request.Updates, result.MainExecutable);
        Directory.CreateDirectory(Path.Combine(bundle, "Contents", "Resources"));
        WriteFile(Path.Combine(bundle, RecordPath), record, RegularMode);
        if (request.Updater is { } updater)
        {
            WriteFile(Path.Combine(bundle, "Contents", "MacOS", UpdaterFileName), updater, ExecutableMode);
        }

        return result;
    }

    private static MacBundleResult Wrap(MacBundleRequest request, string input, string bundle)
    {
        var config = request.Config;
        var main = config.Input.Main.Value;
        if (main.Contains('/', StringComparison.Ordinal))
        {
            throw new MacPackException(
                $"input.main: '{main}' is in a subdirectory, but a bundle's main executable is a file in Contents/MacOS; "
                + "make input.dir the directory that holds it, or pack an existing .app.");
        }

        if (!FatMachO.IsMachOFile(config.Input.Main.FullPath))
        {
            throw new MacPackException($"input.main: {config.Input.Main.FullPath} is not a Mach-O executable, which a macOS bundle needs as its main executable.");
        }

        var macOS = Path.Combine(bundle, "Contents", "MacOS");
        var resources = Path.Combine(bundle, "Contents", "Resources");
        CreateDirectory(bundle);
        CreateDirectory(Path.Combine(bundle, "Contents"));
        CreateDirectory(macOS);
        CreateDirectory(resources);
        var app = Path.Combine(bundle, ResourcesAppDirectory);
        CreateDirectory(app);
        var exclude = request.Exclude is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Exclude)) + Path.DirectorySeparatorChar;
        var links = new List<(string Relative, string Target)>();
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };

        void Visit(string directory, string relative)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", options).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                var itemRelative = relative.Length == 0 ? name : relative + "/" + name;
                if (relative.Length == 0 && name == UpdaterFileName && request.Updater is not null)
                {
                    throw new MacPackException($"input.dir: '{name}' is the updater's name in Contents/MacOS; rename the input file.");
                }

                var depth = itemRelative.Count(c => c == '/');
                if (new FileInfo(path).LinkTarget is { } linkTarget)
                {
                    links.Add((itemRelative, linkTarget));
                    File.CreateSymbolicLink(Path.Combine(macOS, itemRelative), linkTarget);
                    File.CreateSymbolicLink(Path.Combine(app, itemRelative), linkTarget);
                }
                else if (Directory.Exists(path))
                {
                    if (exclude is not null && (Path.GetFullPath(path) + Path.DirectorySeparatorChar).StartsWith(exclude, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (MacCodeSigner.BundleExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new MacPackException(
                            $"input.dir: '{itemRelative}' is a nested bundle; zexoro wraps only flat build outputs into a bundle. "
                            + "Build the .app yourself and set input.dir to it.");
                    }

                    CreateDirectory(Path.Combine(macOS, itemRelative));
                    CreateDirectory(Path.Combine(app, itemRelative));
                    Visit(path, itemRelative);
                }
                else if (itemRelative == main || FatMachO.IsMachOFile(path))
                {
                    CopyFile(path, Path.Combine(macOS, itemRelative), itemRelative == main || HasExecuteBit(path) ? ExecutableMode : RegularMode);
                    File.CreateSymbolicLink(Path.Combine(app, itemRelative), Up(depth + 2) + "MacOS/" + itemRelative);
                }
                else
                {
                    CopyFile(path, Path.Combine(app, itemRelative), HasExecuteBit(path) || IsScript(path) ? ExecutableMode : RegularMode);
                    File.CreateSymbolicLink(Path.Combine(macOS, itemRelative), Up(depth + 1) + "Resources/app/" + itemRelative);
                }
            }
        }

        Visit(input, string.Empty);
        foreach (var (relative, target) in links)
        {
            CheckLink(relative, target, "input.dir");
        }

        var icon = request.LoadIcon();
        WriteFile(Path.Combine(resources, IconName + ".icns"), icon, RegularMode);
        WriteFile(Path.Combine(bundle, "Contents", "Info.plist"), PropertyList.ToXml(CreateInfoPlist(config, main)), RegularMode);
        WriteFile(Path.Combine(bundle, "Contents", "PkgInfo"), "APPL????"u8.ToArray(), RegularMode);
        return new MacBundleResult(bundle, "Contents/MacOS/" + main, Generated: true);
    }

    private static MacBundleResult CopyExisting(ZexoroConfig config, string input, string bundle)
    {
        var infoPath = Path.Combine(input, "Contents", "Info.plist");
        if (!File.Exists(infoPath))
        {
            throw new MacPackException($"input.dir: {input} is named .app but has no Contents/Info.plist; give a bundle or a flat build output.");
        }

        Dictionary<string, object> plist;
        try
        {
            plist = PropertyList.Read(File.ReadAllBytes(infoPath));
        }
        catch (PropertyListException ex)
        {
            throw new MacPackException($"input.dir: {infoPath}: {ex.Message}", ex);
        }

        var expectedId = config.MacOS.BundleIdentifier(config.App);
        var idKey = config.MacOS.BundleId is null ? "app.id" : "macos.bundle_id";
        if (plist.GetValueOrDefault("CFBundleIdentifier") is not string id || id != expectedId)
        {
            throw new MacPackException(
                $"input.dir: the bundle's CFBundleIdentifier is '{plist.GetValueOrDefault("CFBundleIdentifier")}', but {idKey} is '{expectedId}'; they must match.");
        }

        var expectedVersion = BundleVersion.ShortVersion(config.App.Version);
        if (plist.GetValueOrDefault("CFBundleShortVersionString") is not string version || version != expectedVersion)
        {
            throw new MacPackException(
                $"input.dir: the bundle's CFBundleShortVersionString is '{plist.GetValueOrDefault("CFBundleShortVersionString")}', "
                + $"but app.version {config.App.Version} needs '{expectedVersion}'; they must match.");
        }

        if (plist.GetValueOrDefault("CFBundleExecutable") is not string executable || executable.Length == 0 || executable.Contains('/', StringComparison.Ordinal))
        {
            throw new MacPackException("input.dir: the bundle's Info.plist has no valid CFBundleExecutable.");
        }

        var main = "Contents/MacOS/" + executable;
        if (config.Input.Main.Value != main)
        {
            throw new MacPackException($"input.main: is '{config.Input.Main.Value}', but the bundle's main executable is '{main}'; set input.main = \"{main}\".");
        }

        var links = new List<(string Relative, string Target)>();
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };

        void Visit(string directory, string relative)
        {
            CreateDirectory(relative.Length == 0 ? bundle : Path.Combine(bundle, relative));
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", options).Order(StringComparer.Ordinal))
            {
                var itemRelative = relative.Length == 0 ? Path.GetFileName(path) : relative + "/" + Path.GetFileName(path);
                if (new FileInfo(path).LinkTarget is { } linkTarget)
                {
                    links.Add((itemRelative, linkTarget));
                    File.CreateSymbolicLink(Path.Combine(bundle, itemRelative), linkTarget);
                }
                else if (Directory.Exists(path))
                {
                    Visit(path, itemRelative);
                }
                else
                {
                    CopyFile(path, Path.Combine(bundle, itemRelative), itemRelative == main || HasExecuteBit(path) ? ExecutableMode : RegularMode);
                }
            }
        }

        Visit(input, string.Empty);
        foreach (var (relative, target) in links)
        {
            CheckLink(relative, target, "input.dir");
        }

        return new MacBundleResult(bundle, main, Generated: false);
    }

    /// <summary>A link must have a relative target that, resolved lexically from its directory, stays inside the root.</summary>
    internal static void CheckLink(string relative, string target, string key)
    {
        var depth = relative.Count(c => c == '/');
        if (target.Length == 0 || target.StartsWith('/') || target.Contains('\0', StringComparison.Ordinal))
        {
            throw new MacPackException($"{key}: the symbolic link '{relative}' points to '{target}'; bundles keep only relative links inside the bundle.");
        }

        foreach (var part in target.Split('/'))
        {
            if (part == "..")
            {
                depth--;
                if (depth < 0)
                {
                    throw new MacPackException($"{key}: the symbolic link '{relative}' points to '{target}', outside the directory; bundles keep only relative links inside the bundle.");
                }
            }
            else if (part is not ("" or "."))
            {
                depth++;
            }
        }
    }

    private static string Up(int levels) => string.Concat(Enumerable.Repeat("../", levels));

    private static bool IsScript(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return stream.ReadByte() == '#' && stream.ReadByte() == '!';
    }

    private static bool HasExecuteBit(string path) =>
        !OperatingSystem.IsWindows()
        && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    private static void CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, DirectoryMode);
        }
    }

    /// <summary>Copies the content only (no extended attributes) and sets the mode.</summary>
    private static void CopyFile(string source, string destination, UnixFileMode mode)
    {
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(output);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destination, mode);
        }
    }

    private static void WriteFile(string path, byte[] content, UnixFileMode mode)
    {
        File.WriteAllBytes(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }
}
