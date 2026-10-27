using Zexoro.Cli.Toolsets;
using Zexoro.Configuration;
using Zexoro.Formats.Nsis;
using Zexoro.Signing;
using Zexoro.Toolsets;
using Zexoro.Updates;
using Zexoro.Versioning;

namespace Zexoro.Cli.Commands;

/// <summary>
/// <c>zexoro pack [--config &lt;path&gt;] [--version &lt;v&gt;] [--output &lt;dir&gt;] [--channel &lt;c&gt;] [--notes &lt;url&gt;]</c>:
/// builds the configured installers and, with <c>[updates]</c>, a release fragment next to each.
/// </summary>
internal static class PackCommand
{
    /// <summary>The default output directory, next to the configuration file.</summary>
    public const string DefaultOutputDirectory = "dist";

    /// <summary>The release fragment's file name suffix: <c>&lt;installer&gt;.release.json</c>.</summary>
    public const string FragmentSuffix = ".release.json";

    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CliEnvironment environment)
    {
        var options = CommandOptions.Parse("pack", args, ["--config", "--version", "--output", "--channel", "--notes"], stdout, stderr, out var exitCode);
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
            config = ZexoroConfig.Load(
                configPath,
                new ConfigOverrides { Version = options.GetValueOrDefault("--version"), Channel = options.GetValueOrDefault("--channel") },
                displayPath);
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

        var notes = options.GetValueOrDefault("--notes");
        if (notes is not null)
        {
            if (config.Updates is null)
            {
                return Fail(stderr, "--notes: there is no [updates] table, so there is no release to describe");
            }

            if (!Uri.TryCreate(notes, UriKind.Absolute, out var notesUrl) || (notesUrl.Scheme != Uri.UriSchemeHttps && notesUrl.Scheme != Uri.UriSchemeHttp))
            {
                return Fail(stderr, $"--notes: '{notes}' is not an absolute http or https URL");
            }
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
                ResolveUpdater = architecture => BundledUpdaters.Resolve(architecture, environment),
            });
            stdout.WriteLine($"Created {result.InstallerPath}");
            if (config.Updates is { } updates)
            {
                var fragment = WriteFragment(config, updates, result, notes);
                stdout.WriteLine($"Created {fragment} (channel {updates.Channel}); publish it with 'zexoro feed add'");
            }

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
        catch (UpdateException ex)
        {
            return Fail(stderr, $"cannot write the release fragment: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(stderr, $"cannot write the release fragment: {ex.Message}");
        }
        finally
        {
            signing?.Credential.Dispose();
        }
    }

    /// <summary>
    /// Writes <c>&lt;installer&gt;.release.json</c> (docs/design/0005-nsis-updates.md section 2): the
    /// release with one artifact, the final (signed) installer, for <c>zexoro feed add</c>.
    /// </summary>
    internal static string WriteFragment(ZexoroConfig config, UpdatesConfig updates, NsisPackResult result, string? notes)
    {
        var installer = result.InstallerPath;
        long size;
        string sha256;
        using (var stream = File.OpenRead(installer))
        {
            size = stream.Length;
            sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
        }

        var spec = result.Spec;
        var target = new UpdateTarget("windows", spec.Architecture.Name, "nsis", spec.ScopeName);
        var url = updates.DownloadUrl(Path.GetFileName(installer), config.App.Version.ToString());
        var release = new FeedRelease(
            config.App.Version,
            [new FeedArtifact(target, url, size, sha256)],
            notes,
            SemanticVersion.Parse(NsisScript.MinimumUpdater));
        var path = installer + FragmentSuffix;
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, new ReleaseFragment(config.App.Id, updates.Channel, release).ToJson());
        File.Move(temporary, path, overwrite: true);
        return path;
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
