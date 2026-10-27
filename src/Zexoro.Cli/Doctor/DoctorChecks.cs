using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Zexoro.Cli.Packaging;
using Zexoro.Cli.Toolsets;
using Zexoro.Configuration;
using Zexoro.Formats.Linux;
using Zexoro.Formats.Nsis;
using Zexoro.Platforms;
using Zexoro.Signing;
using Zexoro.Toolsets;

namespace Zexoro.Cli.Doctor;

/// <summary>A check that runs once the configuration has loaded.</summary>
internal interface IDoctorCheck
{
    void Run(DoctorContext context, DoctorReport report);
}

/// <summary>
/// What the checks look at: the configuration, and the platforms and formats that <c>pack</c>
/// would build. When the target platform cannot be determined yet (the main executable does not
/// exist), every platform whose table is written in the configuration counts.
/// </summary>
internal sealed class DoctorContext
{
    public DoctorContext(ZexoroConfig config, CliEnvironment environment)
    {
        Config = config;
        Environment = environment;
        if (Directory.Exists(config.Input.Directory.FullPath) && File.Exists(config.Input.Main.FullPath))
        {
            try
            {
                Target = config.ResolveTarget();
            }
            catch (ConfigurationException ex)
            {
                TargetErrors = ex.Errors.Select(e => e.ToString()).ToList();
            }
        }

        Platforms = Target is { } target
            ? [config.Platform(target.Platform.OperatingSystem)!]
            : PackagingTargets.All.Select(t => t.OperatingSystem).Distinct()
                .Select(os => config.Platform(os)!).Where(p => p.IsConfigured).ToList();
        Formats = Platforms.SelectMany(p => p.Formats).ToList();
    }

    public ZexoroConfig Config { get; }

    public CliEnvironment Environment { get; }

    /// <summary>The target platform, or null when it cannot be determined.</summary>
    public PackTarget? Target { get; }

    /// <summary>Why the target platform cannot be determined although the main file exists; empty otherwise.</summary>
    public IReadOnlyList<string> TargetErrors { get; } = [];

    /// <summary>The platform tables that apply.</summary>
    public IReadOnlyList<PlatformConfig> Platforms { get; }

    /// <summary>The formats of <see cref="Platforms"/>, as configured (<c>pack --format</c> may choose others).</summary>
    public IReadOnlyList<PackageFormat> Formats { get; }

    /// <summary>Whether packages for <paramref name="operatingSystem"/> would be built.</summary>
    public bool Targets(OperatingSystemFamily operatingSystem) => Platforms.Any(p => p.OperatingSystem == operatingSystem);
}

/// <summary>The checks <c>zexoro doctor</c> runs, in order.</summary>
internal static class DoctorChecks
{
    /// <summary>
    /// Checks that need a valid configuration. Extension point: checks of the bundled toolsets are
    /// added to this list together with the back ends that ship them.
    /// </summary>
    public static readonly IReadOnlyList<IDoctorCheck> All =
    [
        new InputCheck(),
        new TargetsCheck(),
        new IconCheck(),
        new WindowsLicenseCheck(),
        new WindowsSigningCheck(),
        new NsisToolsetCheck(),
        new UpdaterCheck(),
        new UpdatesCheck(),
    ];

    public static void Run(string configPath, string displayPath, CliEnvironment environment, DoctorReport report)
    {
        if (!File.Exists(configPath))
        {
            report.Error($"{displayPath}: not found; run 'zexoro init' to create a configuration");
            return;
        }

        ZexoroConfig config;
        try
        {
            config = ZexoroConfig.Load(configPath, source: displayPath);
        }
        catch (ConfigurationException ex)
        {
            foreach (var error in ex.Errors)
            {
                report.Error(error.ToString());
            }

            return;
        }

        report.Ok($"{displayPath}: configuration is valid ({config.App.Id} {config.App.Version})");
        var context = new DoctorContext(config, environment);
        foreach (var check in All)
        {
            check.Run(context, report);
        }
    }

    /// <summary>Formats a message about a path: <c>source:line:column: key: message (absolute path)</c>.</summary>
    internal static string About(ConfigPath path, ZexoroConfig config, string message) =>
        $"{config.Source}:{path.Position.Line}:{path.Position.Column}: {path.Key}: {message} ({path.FullPath})";

    /// <summary>Formats a message about a value: <c>source:line:column: key: message</c>.</summary>
    internal static string About(ConfigValue value, string message) =>
        $"{value.Source}:{value.Position.Line}:{value.Position.Column}: {value.Key}: {message}";
}

/// <summary>Reports the target platform and the formats <c>pack</c> builds for it.</summary>
internal sealed class TargetsCheck : IDoctorCheck
{
    public void Run(DoctorContext context, DoctorReport report)
    {
        foreach (var error in context.TargetErrors)
        {
            report.Error(error);
        }

        if (context.Target is not { } target)
        {
            if (context.TargetErrors.Count == 0 && context.Platforms.Count == 0)
            {
                report.Warning("the target platform is unknown until input.main exists, and no platform table ([windows], [linux] or [macos]) is configured");
            }

            return;
        }

        var errors = new List<string>();
        var formats = FormatSelection.Select(context.Config, target, null, errors);
        foreach (var error in errors)
        {
            report.Error(error);
        }

        if (formats is not null)
        {
            report.Ok($"target {target.Platform} ({target.Origin}); formats: {string.Join(", ", formats.Select(f => f.Name))}");
        }
    }
}

/// <summary>The input directory and main executable exist.</summary>
internal sealed class InputCheck : IDoctorCheck
{
    public void Run(DoctorContext context, DoctorReport report)
    {
        var config = context.Config;
        var dir = config.Input.Directory;
        if (!Directory.Exists(dir.FullPath))
        {
            report.Error(DoctorChecks.About(dir, config, $"directory '{dir.Value}' not found; build your application first"));
            return;
        }

        report.Ok($"input.dir: {dir.FullPath}");

        var main = config.Input.Main;
        if (!File.Exists(main.FullPath))
        {
            report.Error(DoctorChecks.About(main, config, $"'{main.Value}' not found in the input directory"));
            return;
        }

        report.Ok($"input.main: {main.FullPath}");
        if (context.Targets(OperatingSystemFamily.Windows) && !main.Value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            report.Warning(DoctorChecks.About(main, config, "Windows installers expect an .exe entry point"));
        }
    }
}

/// <summary>The application icon exists and, for Windows, is an ICO file; for Linux, a PNG icon can be made.</summary>
internal sealed class IconCheck : IDoctorCheck
{
    public void Run(DoctorContext context, DoctorReport report)
    {
        var config = context.Config;
        var icon = config.App.Icon;
        if (!File.Exists(icon.FullPath))
        {
            report.Error(DoctorChecks.About(icon, config, $"'{icon.Value}' not found"));
            return;
        }

        if (context.Targets(OperatingSystemFamily.Windows) && !IsIco(icon.FullPath))
        {
            report.Error(DoctorChecks.About(icon, config, $"'{icon.Value}' is not an .ico file, which Windows installers need"));
            return;
        }

        report.Ok($"app.icon: {icon.FullPath}");
        if (context.Targets(OperatingSystemFamily.Linux))
        {
            CheckLinuxIcon(config, report);
        }
    }

    /// <summary>Linux formats need a PNG: <c>[linux] icon</c>, or one converted from <c>[app] icon</c>.</summary>
    private static void CheckLinuxIcon(ZexoroConfig config, DoctorReport report)
    {
        try
        {
            var png = LinuxTarPackager.LoadIcon(config);
            var source = config.Linux.Icon is { } linuxIcon ? $"linux.icon: {linuxIcon.FullPath}" : "linux icon: converted from app.icon";
            report.Ok($"{source} ({png.Width}x{png.Height} PNG)");
        }
        catch (TarPackException ex)
        {
            report.Error(ex.Message);
        }
    }

    /// <summary>An ICO file starts with reserved 0, type 1 (icon) and a non-zero image count, all little-endian.</summary>
    internal static bool IsIco(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[6];
            return stream.ReadAtLeast(header, 6, throwOnEndOfStream: false) == 6
                && header[0] == 0 && header[1] == 0
                && header[2] == 1 && header[3] == 0
                && (header[4] != 0 || header[5] != 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>The license file exists, if one is configured.</summary>
internal sealed class WindowsLicenseCheck : IDoctorCheck
{
    public void Run(DoctorContext context, DoctorReport report)
    {
        var config = context.Config;
        if (!context.Targets(OperatingSystemFamily.Windows) || config.Windows.License is not { } license)
        {
            return;
        }

        if (File.Exists(license.FullPath))
        {
            report.Ok($"windows.license: {license.FullPath}");
        }
        else
        {
            report.Error(DoctorChecks.About(license, config, $"'{license.Value}' not found"));
        }
    }
}

/// <summary>The signing certificate loads with its password; the timestamp URL resolves.</summary>
internal sealed class WindowsSigningCheck : IDoctorCheck
{
    private const string CodeSigningEku = "1.3.6.1.5.5.7.3.3";

    public void Run(DoctorContext context, DoctorReport report)
    {
        var config = context.Config;
        var environment = context.Environment;
        if (!context.Targets(OperatingSystemFamily.Windows))
        {
            return;
        }

        if (config.Windows.Signing is not { } signing)
        {
            report.Warning("[windows.signing] is not configured; installers will be unsigned");
            return;
        }

        CheckCertificate(signing, environment, report);
        CheckTimestamp(signing, environment, report);
    }

    private static void CheckCertificate(WindowsSigningConfig signing, CliEnvironment environment, DoctorReport report)
    {
        string path;
        string? password;
        try
        {
            path = signing.ResolveCertificatePath(environment.GetVariable);
            password = signing.ResolvePassword(environment.GetVariable);
        }
        catch (ConfigurationException ex)
        {
            foreach (var error in ex.Errors)
            {
                report.Error(error.ToString());
            }

            return;
        }

        if (signing.Password is { IsEnvironmentReference: false } literal)
        {
            report.Warning(DoctorChecks.About(literal, "the password is written in the configuration file; prefer { env = \"NAME\" }"));
        }

        if (!File.Exists(path))
        {
            report.Error(DoctorChecks.About(signing.Certificate, $"certificate file {path} not found"));
            return;
        }

        SigningCredential credential;
        try
        {
            credential = SigningCredential.LoadPfx(path, password);
        }
        catch (SigningException ex)
        {
            report.Error(DoctorChecks.About(signing.Certificate, ex.Message));
            return;
        }

        using (credential)
        {
            var certificate = credential.Certificate;
            var now = DateTime.Now;
            var expires = certificate.NotAfter.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (certificate.NotBefore > now)
            {
                report.Error($"{signing.Certificate.Key}: certificate '{certificate.Subject}' is not valid until {certificate.NotBefore.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
                return;
            }

            if (certificate.NotAfter < now)
            {
                report.Error($"{signing.Certificate.Key}: certificate '{certificate.Subject}' expired on {expires}");
                return;
            }

            report.Ok($"signing certificate '{certificate.Subject}' loads (expires {expires})");
            if (certificate.NotAfter < now.AddDays(30))
            {
                report.Warning($"{signing.Certificate.Key}: certificate '{certificate.Subject}' expires within 30 days ({expires})");
            }

            var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
            if (eku is not null && !eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(o => o.Value == CodeSigningEku))
            {
                report.Warning($"{signing.Certificate.Key}: certificate '{certificate.Subject}' is not marked for code signing; Windows may not accept its signatures");
            }
        }
    }

    private static void CheckTimestamp(WindowsSigningConfig signing, CliEnvironment environment, DoctorReport report)
    {
        Uri? url;
        try
        {
            url = signing.ResolveTimestamp(environment.GetVariable);
        }
        catch (ConfigurationException ex)
        {
            foreach (var error in ex.Errors)
            {
                report.Error(error.ToString());
            }

            return;
        }

        if (url is null)
        {
            report.Warning("windows.signing.timestamp is not set; signatures stop being valid when the certificate expires");
        }
        else
        {
            report.Ok($"timestamp authority {url} (not contacted)");
        }
    }
}

/// <summary>The NSIS toolset extracts, matches its pinned hash and runs.</summary>
internal sealed class NsisToolsetCheck : IDoctorCheck
{
    public void Run(DoctorContext context, DoctorReport report)
    {
        var environment = context.Environment;
        if (!context.Formats.Contains(PackageFormat.Nsis))
        {
            return;
        }

        try
        {
            var toolset = BundledToolsets.ResolveNsis(environment);
            var version = toolset.GetVersion();
            report.Ok($"{toolset.Source}: makensis {version} ({toolset.Directory})");
        }
        catch (Exception ex) when (ex is NsisException or ToolsetException)
        {
            report.Error($"NSIS toolset: {ex.Message}");
        }
    }
}

/// <summary>
/// Reports every zexoro-updater build that is available (embedded or from ZEXORO_UPDATER_DIR); a
/// missing one is a problem only for an operating system the configuration targets.
/// </summary>
internal sealed class UpdaterCheck : IDoctorCheck
{
    public void Run(DoctorContext context, DoctorReport report)
    {
        var config = context.Config;
        var environment = context.Environment;
        var fromDirectory = environment.GetVariable(UpdaterRuntime.DirectoryVariable) is { Length: > 0 };
        var missing = new List<(UpdaterRuntime Runtime, string Problem)>();
        foreach (var runtime in UpdaterRuntime.All)
        {
            try
            {
                var updater = BundledUpdaters.Resolve(runtime, environment);
                report.Ok($"{updater.Source} (sha256 {updater.Sha256})");
            }
            catch (Exception ex) when (ex is UpdaterUnavailableException or IOException or UnauthorizedAccessException)
            {
                missing.Add((runtime, fromDirectory ? ex.Message : $"this zexoro build has no embedded updater for {runtime.Rid}"));
            }
        }

        // Only packages with [updates] need an updater.
        Action<string> reportProblem = config.Updates is null ? report.Warning : report.Error;
        var relevant = missing.Where(m => context.Targets(m.Runtime.Platform.OperatingSystem)).ToList();
        if (!fromDirectory && missing.Count == UpdaterRuntime.All.Count)
        {
            if (relevant.Count > 0)
            {
                reportProblem($"this zexoro build has no embedded updaters; packages with [updates] need {UpdaterRuntime.DirectoryVariable}");
            }

            return;
        }

        foreach (var (_, problem) in relevant)
        {
            reportProblem($"updater: {problem}");
        }
    }
}

/// <summary><c>[updates]</c>: the key files load; reports the feed URL and channel.</summary>
internal sealed class UpdatesCheck : IDoctorCheck
{
    public void Run(DoctorContext context, DoctorReport report)
    {
        var config = context.Config;
        if (config.Updates is not { } updates)
        {
            return;
        }

        try
        {
            var keys = updates.ResolveKeys();
            report.Ok($"updates: channel {updates.Channel}, feed {updates.FeedUrl.AbsoluteUri}, {keys.Count} trusted key(s)");
        }
        catch (ConfigurationException ex)
        {
            foreach (var error in ex.Errors)
            {
                report.Error(error.ToString());
            }
        }
    }
}
