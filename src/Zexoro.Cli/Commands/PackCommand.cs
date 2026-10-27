using Zexoro.Cli.Toolsets;
using Zexoro.Configuration;
using Zexoro.Formats.Nsis;
using Zexoro.Signing;
using Zexoro.Toolsets;

namespace Zexoro.Cli.Commands;

/// <summary><c>zexoro pack [--config &lt;path&gt;] [--version &lt;v&gt;] [--output &lt;dir&gt;]</c>: builds the configured installers.</summary>
internal static class PackCommand
{
    /// <summary>The default output directory, next to the configuration file.</summary>
    public const string DefaultOutputDirectory = "dist";

    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CliEnvironment environment)
    {
        var options = CommandOptions.Parse("pack", args, ["--config", "--version", "--output"], stdout, stderr, out var exitCode);
        if (options is null)
        {
            return exitCode;
        }

        var displayPath = options.GetValueOrDefault("--config") ?? ZexoroConfig.DefaultFileName;
        var configPath = Path.GetFullPath(displayPath, environment.WorkingDirectory);
        if (!File.Exists(configPath))
        {
            return Fail(stderr, $"{displayPath}: not found; run 'zexoro init' to create a configuration");
        }

        ZexoroConfig config;
        try
        {
            config = ZexoroConfig.Load(configPath, new ConfigOverrides { Version = options.GetValueOrDefault("--version") }, displayPath);
        }
        catch (ConfigurationException ex)
        {
            foreach (var error in ex.Errors)
            {
                stderr.WriteLine($"zexoro: {error}");
            }

            return ExitCodes.Failure;
        }

        if (config.Windows is null)
        {
            return Fail(stderr, $"{displayPath}: no target platform is configured; add a [windows] table to build a Windows installer");
        }

        var output = options.TryGetValue("--output", out var outputOption)
            ? Path.GetFullPath(outputOption, environment.WorkingDirectory)
            : Path.Combine(config.BaseDirectory, DefaultOutputDirectory);

        NsisSigning? signing = null;
        try
        {
            if (config.Windows.Signing is { } signingConfig)
            {
                signing = LoadSigning(signingConfig, environment);
            }
            else
            {
                stdout.WriteLine("notice: [windows.signing] is not configured; the installer and the application are not signed.");
            }

            var toolset = BundledToolsets.ResolveNsis(environment);
            stdout.WriteLine($"Packing {config.App.Name} {config.App.Version} ({config.App.Id})");
            var result = NsisPackager.Pack(new NsisPackRequest
            {
                Config = config,
                Toolset = toolset,
                OutputDirectory = output,
                Signing = signing,
                Log = stdout,
            });
            stdout.WriteLine($"Created {result.InstallerPath}");
            return ExitCodes.Success;
        }
        catch (ConfigurationException ex)
        {
            foreach (var error in ex.Errors)
            {
                stderr.WriteLine($"zexoro: {error}");
            }

            return ExitCodes.Failure;
        }
        catch (Exception ex) when (ex is NsisException or ToolsetException)
        {
            return Fail(stderr, ex.Message);
        }
        finally
        {
            signing?.Credential.Dispose();
        }
    }

    private static NsisSigning LoadSigning(WindowsSigningConfig signing, CliEnvironment environment)
    {
        var path = signing.ResolveCertificatePath(environment.GetVariable);
        var password = signing.ResolvePassword(environment.GetVariable);
        var timestamp = signing.ResolveTimestamp(environment.GetVariable);
        if (!File.Exists(path))
        {
            throw new NsisException($"{signing.Certificate.Key}: certificate file {path} not found.");
        }

        SigningCredential credential;
        try
        {
            credential = SigningCredential.LoadPfx(path, password);
        }
        catch (SigningException ex)
        {
            throw new NsisException($"{signing.Certificate.Key}: {ex.Message}", ex);
        }

        return new NsisSigning
        {
            Credential = credential,
            CertificatePath = path,
            Password = password,
            Timestamp = timestamp,
            ZexoroCommand = environment.ZexoroCommand,
        };
    }

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine($"zexoro: {message}");
        return ExitCodes.Failure;
    }
}
