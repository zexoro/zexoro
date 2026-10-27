using System.Globalization;

namespace Zexoro.Updater;

/// <summary>Command-line entry point, kept free of <see cref="Console"/> so it can be tested.</summary>
internal static class UpdaterApp
{
    public const string Usage = """
        Usage: zexoro-updater check    [--json]
               zexoro-updater download [--json]
               zexoro-updater apply    [--wait-pid <pid>] [--restart] [--json]
               zexoro-updater [-h | --help | -v | --version]

        Commands:
          check      Fetch and verify the feed; report whether an update is available.
          download   Like check, then download and verify the update for apply.
          apply      Install the downloaded update. --wait-pid waits up to 60 seconds for
                     that process (the application) to exit first; --restart starts the
                     application afterwards.

        --json writes one JSON object to standard output; messages go to standard error.
        Installs owned by a package manager (deb, rpm, apk) are only checked: download
        and apply exit 7, and the new package is installed with the package manager.

        Exit codes: 0 update available / downloaded / applied, 1 up to date or nothing
        downloaded, 2 usage error, 3 network failure, 4 verification failed, 5 apply failed
        and was rolled back, 6 cancelled, 7 manual update needed, 8 apply and rollback failed.
        """;

    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, UpdaterEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 1 && args[0] is "-h" or "--help")
        {
            stdout.WriteLine(Usage);
            return UpdaterExitCodes.Success;
        }

        if (args.Count == 1 && args[0] is "-v" or "--version")
        {
            stdout.WriteLine(ProductInfo.Version);
            return UpdaterExitCodes.Success;
        }

        if (!TryParse(args, out var command, out var options, out var error))
        {
            stderr.WriteLine($"zexoro-updater: {error}");
            stderr.WriteLine("Run 'zexoro-updater --help' for usage.");
            return UpdaterExitCodes.UsageError;
        }

        var result = command == "apply"
            ? new ApplyCommand(environment, stderr).Run(options)
            : new UpdateCommands(environment, stderr).CheckAsync(download: command == "download", CancellationToken.None).GetAwaiter().GetResult();
        if (!result.Relayed)
        {
            if (options.Json)
            {
                stdout.WriteLine(System.Text.Encoding.UTF8.GetString(result.ToJson()));
            }

            if (result.Status == UpdaterResult.Error)
            {
                stderr.WriteLine($"zexoro-updater: {result.Message}");
            }
            else if (options.Json)
            {
                stderr.WriteLine(result.Message);
            }
            else
            {
                stdout.WriteLine(result.Message);
            }
        }

        return result.ExitCode;
    }

    internal static bool TryParse(IReadOnlyList<string> args, out string command, out ApplyOptions options, out string error)
    {
        options = new ApplyOptions(null, false, false, null);
        command = args.Count > 0 ? args[0] : string.Empty;
        error = string.Empty;
        if (command is not ("check" or "download" or "apply"))
        {
            error = args.Count == 0 ? "no command given" : $"unknown command '{command}'";
            return false;
        }

        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--json":
                    options = options with { Json = true };
                    break;
                case "--restart" when command == "apply":
                    options = options with { Restart = true };
                    break;
                case "--wait-pid" when command == "apply":
                    if (i + 1 >= args.Count || !int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
                    {
                        error = "--wait-pid needs a process id";
                        return false;
                    }

                    options = options with { WaitPid = pid };
                    break;
                case "--from-root" when command == "apply":
                    // Internal: the copy in %TEMP% is told which install it updates.
                    if (i + 1 >= args.Count || args[++i].Length == 0 || !Path.IsPathFullyQualified(args[i]))
                    {
                        error = "--from-root needs an absolute directory";
                        return false;
                    }

                    options = options with { FromRoot = args[i] };
                    break;
                default:
                    error = $"unknown option '{arg}' for {command}";
                    return false;
            }
        }

        return true;
    }
}
