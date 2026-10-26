using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using Zexoro.Configuration;
using Zexoro.Signing;

namespace Zexoro.Cli.Doctor;

/// <summary>A check that runs once the configuration has loaded.</summary>
internal interface IDoctorCheck
{
    void Run(ZexoroConfig config, CliEnvironment environment, DoctorReport report);
}

/// <summary>The checks <c>zexoro doctor</c> runs, in order.</summary>
internal static class DoctorChecks
{
    /// <summary>
    /// Checks that need a valid configuration. Extension point: checks of the bundled toolsets
    /// (for example the extracted <c>makensis</c> and its hash) are added to this list together
    /// with the back ends that ship them.
    /// </summary>
    public static readonly IReadOnlyList<IDoctorCheck> All =
    [
        new TargetsCheck(),
        new InputCheck(),
        new IconCheck(),
        new WindowsLicenseCheck(),
        new WindowsSigningCheck(),
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
        foreach (var check in All)
        {
            check.Run(config, environment, report);
        }
    }

    /// <summary>Formats a message about a path: <c>source:line:column: key: message (absolute path)</c>.</summary>
    internal static string About(ConfigPath path, ZexoroConfig config, string message) =>
        $"{config.Source}:{path.Position.Line}:{path.Position.Column}: {path.Key}: {message} ({path.FullPath})";

    /// <summary>Formats a message about a value: <c>source:line:column: key: message</c>.</summary>
    internal static string About(ConfigValue value, string message) =>
        $"{value.Source}:{value.Position.Line}:{value.Position.Column}: {value.Key}: {message}";
}

/// <summary>Reports which platforms the configuration targets.</summary>
internal sealed class TargetsCheck : IDoctorCheck
{
    public void Run(ZexoroConfig config, CliEnvironment environment, DoctorReport report)
    {
        if (config.Windows is null)
        {
            report.Warning("no target platform is configured; add a [windows] table to build a Windows installer");
        }
    }
}

/// <summary>The input directory and main executable exist.</summary>
internal sealed class InputCheck : IDoctorCheck
{
    public void Run(ZexoroConfig config, CliEnvironment environment, DoctorReport report)
    {
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
        if (config.Windows is not null && !main.Value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            report.Warning(DoctorChecks.About(main, config, "Windows installers expect an .exe entry point"));
        }
    }
}

/// <summary>The application icon exists and, for Windows, is an ICO file.</summary>
internal sealed class IconCheck : IDoctorCheck
{
    public void Run(ZexoroConfig config, CliEnvironment environment, DoctorReport report)
    {
        var icon = config.App.Icon;
        if (!File.Exists(icon.FullPath))
        {
            report.Error(DoctorChecks.About(icon, config, $"'{icon.Value}' not found"));
            return;
        }

        if (config.Windows is not null && !IsIco(icon.FullPath))
        {
            report.Error(DoctorChecks.About(icon, config, $"'{icon.Value}' is not an .ico file, which Windows installers need"));
            return;
        }

        report.Ok($"app.icon: {icon.FullPath}");
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
    public void Run(ZexoroConfig config, CliEnvironment environment, DoctorReport report)
    {
        if (config.Windows?.License is not { } license)
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

    public void Run(ZexoroConfig config, CliEnvironment environment, DoctorReport report)
    {
        if (config.Windows is null)
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
