using Zexoro.Cli.Commands;

namespace Zexoro.Cli;

/// <summary>Command-line entry point, kept free of <see cref="Console"/> so it can be tested.</summary>
internal static class App
{
    public const string Usage = """
        Usage: zexoro <command> [options]
               zexoro [-h | --help | -v | --version]

        Commands:
          init [--force]             Create a commented zexoro.toml in the current directory.
                                     --force overwrites an existing file.
          doctor [--config <path>]   Check the configuration, the files it names and the
                                     signing certificate. --config defaults to ./zexoro.toml.

        Options:
          -h, --help       Show this help.
          -v, --version    Show the zexoro version.

        Exit codes: 0 success, 1 failure, 2 usage error.
        """;

    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr) =>
        Run(args, stdout, stderr, CliEnvironment.Process);

    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CliEnvironment environment)
    {
        if (args.Count == 0)
        {
            stdout.WriteLine(Usage);
            return ExitCodes.Success;
        }

        var rest = args.Skip(1).ToList();
        switch (args[0])
        {
            case "-h" or "--help" when args.Count == 1:
                stdout.WriteLine(Usage);
                return ExitCodes.Success;
            case "-v" or "--version" when args.Count == 1:
                stdout.WriteLine(ProductInfo.Version);
                return ExitCodes.Success;
            case "init":
                return InitCommand.Run(rest, stdout, stderr, environment);
            case "doctor":
                return DoctorCommand.Run(rest, stdout, stderr, environment);
        }

        return UsageError(stderr, $"unrecognized arguments: {string.Join(' ', args)}");
    }

    public static int UsageError(TextWriter stderr, string message)
    {
        stderr.WriteLine($"zexoro: {message}");
        stderr.WriteLine("Run 'zexoro --help' for usage.");
        return ExitCodes.UsageError;
    }
}
