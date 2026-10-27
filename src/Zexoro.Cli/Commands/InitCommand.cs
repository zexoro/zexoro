using System.Text;
using Zexoro.Configuration;

namespace Zexoro.Cli.Commands;

/// <summary><c>zexoro init [--force]</c>: writes the configuration template.</summary>
internal static class InitCommand
{
    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CliEnvironment environment)
    {
        var force = false;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--force":
                    force = true;
                    break;
                case "-h" or "--help":
                    stdout.WriteLine(App.Usage);
                    return ExitCodes.Success;
                default:
                    return App.UsageError(stderr, $"init: unrecognized argument: {arg}");
            }
        }

        var path = Path.Combine(environment.WorkingDirectory, ZexoroConfig.DefaultFileName);
        if (!force && File.Exists(path))
        {
            stderr.WriteLine($"zexoro: {ZexoroConfig.DefaultFileName} already exists; use 'zexoro init --force' to overwrite it.");
            return ExitCodes.Failure;
        }

        try
        {
            File.WriteAllText(path, ConfigTemplate.Text.ReplaceLineEndings("\n"), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"zexoro: cannot write {path}: {ex.Message}");
            return ExitCodes.Failure;
        }

        stdout.WriteLine($"Created {path}.");
        stdout.WriteLine("Fill in [app] and [input], then run 'zexoro doctor'.");
        return ExitCodes.Success;
    }
}
