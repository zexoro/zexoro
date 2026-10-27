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
          pack [--config <path>] [--version <v>] [--output <dir>]
               [--format <f>[,<f>...]] [--input <dir>] [--main <path>]
               [--channel <channel>] [--notes <url>]
                                     Package the build output for its target platform (read
                                     from the main executable, or [input] target) into
                                     --output (default: dist/ next to the configuration
                                     file). --format overrides the platform table's
                                     formats, --input and --main override input.dir and
                                     input.main, --version overrides app.version. With
                                     [updates], also write <package>.release.json for
                                     'feed add'; --channel overrides updates.channel,
                                     --notes sets the release notes URL.
          doctor [--config <path>]   Check the configuration, the files it names, the
                                     signing certificate and the bundled tools.
          keys new [--output <dir>] [--name <name>]
                                     Create an update signing key pair: <name>.key.pem
                                     (private; encrypted when ZEXORO_UPDATE_KEY_PASSWORD
                                     is set) and <name>.pub. Default name: zexoro-update.
          feed add --feed <feed.json> [--key <file>] <fragment.json>...
                                     Merge release fragments into a feed (created when
                                     missing), increase its sequence and sign it into
                                     <feed.json>.sig. The private key comes from --key or
                                     ZEXORO_UPDATE_KEY (PEM text).
          feed verify --public-key <key or .pub file> <feed.json>
                                     Check a feed's signature and list its releases.

        --config defaults to ./zexoro.toml.

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
            case "pack":
                return PackCommand.Run(rest, stdout, stderr, environment);
            case "doctor":
                return DoctorCommand.Run(rest, stdout, stderr, environment);
            case "keys":
                return KeysCommand.Run(rest, stdout, stderr, environment);
            case "feed":
                return FeedCommand.Run(rest, stdout, stderr, environment);
            case "sign-file":
                return SignFileCommand.Run(rest, stdout, stderr, environment);
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
