using Zexoro.Cli.Packaging;
using Zexoro.Configuration;

namespace Zexoro.Cli.Commands;

/// <summary>
/// <c>zexoro pack [--config &lt;path&gt;] [--version &lt;v&gt;] [--output &lt;dir&gt;] [--channel &lt;c&gt;] [--notes &lt;url&gt;]
/// [--format &lt;f&gt;[,&lt;f&gt;…]] [--input &lt;dir&gt;] [--main &lt;path&gt;]</c>: packs one build output for its
/// target platform into each selected format and, with <c>[updates]</c>, writes a release fragment
/// next to each package.
/// </summary>
internal static class PackCommand
{
    /// <summary>The default output directory, next to the configuration file.</summary>
    public const string DefaultOutputDirectory = "dist";

    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CliEnvironment environment)
    {
        var options = CommandOptions.Parse(
            "pack", args, ["--config", "--version", "--output", "--channel", "--notes", "--format", "--input", "--main"], stdout, stderr, out var exitCode);
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
        PackTarget target;
        try
        {
            config = ZexoroConfig.Load(
                configPath,
                new ConfigOverrides
                {
                    Version = options.GetValueOrDefault("--version"),
                    Channel = options.GetValueOrDefault("--channel"),
                    InputDirectory = options.TryGetValue("--input", out var input) ? Path.GetFullPath(input, environment.WorkingDirectory) : null,
                    Main = options.GetValueOrDefault("--main"),
                },
                displayPath);
            target = config.ResolveTarget();
        }
        catch (ConfigurationException ex)
        {
            return Fail(stderr, ex);
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

        var errors = new List<string>();
        var formats = FormatSelection.Select(config, target, options.GetValueOrDefault("--format"), errors);
        if (formats is null)
        {
            foreach (var error in errors)
            {
                stderr.WriteLine($"zexoro: {error}");
            }

            return ExitCodes.Failure;
        }

        var output = options.TryGetValue("--output", out var outputOption)
            ? Path.GetFullPath(outputOption, environment.WorkingDirectory)
            : Path.Combine(config.BaseDirectory, DefaultOutputDirectory);

        stdout.WriteLine($"Packing {config.App.Name} {config.App.Version} ({config.App.Id}) for {target.Platform}: {string.Join(", ", formats.Select(f => f.Name))}");
        var context = new PackContext
        {
            Config = config,
            Target = target.Platform,
            OutputDirectory = output,
            Notes = notes,
            Log = stdout,
            Environment = environment,
        };
        try
        {
            foreach (var format in formats)
            {
                Packagers.Find(format)!.Pack(context);
            }

            return ExitCodes.Success;
        }
        catch (ConfigurationException ex)
        {
            return Fail(stderr, ex);
        }
        catch (PackException ex)
        {
            return Fail(stderr, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(stderr, ex.Message);
        }
    }

    private static int Fail(TextWriter stderr, ConfigurationException ex)
    {
        foreach (var error in ex.Errors)
        {
            stderr.WriteLine($"zexoro: {error}");
        }

        return ExitCodes.Failure;
    }

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine($"zexoro: {message}");
        return ExitCodes.Failure;
    }
}
