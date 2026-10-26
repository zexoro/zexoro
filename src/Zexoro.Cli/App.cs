namespace Zexoro.Cli;

/// <summary>Command-line entry point, kept free of <see cref="Console"/> so it can be tested.</summary>
internal static class App
{
    private const string Usage = """
        Usage: zexoro [options]

        Options:
          -h, --help       Show this help.
          -v, --version    Show the zexoro version.
        """;

    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Count == 0)
        {
            stdout.WriteLine(Usage);
            return ExitCodes.Success;
        }

        if (args.Count == 1)
        {
            switch (args[0])
            {
                case "-h" or "--help":
                    stdout.WriteLine(Usage);
                    return ExitCodes.Success;
                case "-v" or "--version":
                    stdout.WriteLine(ProductInfo.Version);
                    return ExitCodes.Success;
            }
        }

        stderr.WriteLine($"zexoro: unrecognized arguments: {string.Join(' ', args)}");
        stderr.WriteLine("Run 'zexoro --help' for usage.");
        return ExitCodes.UsageError;
    }
}
