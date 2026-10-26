using Zexoro.Cli.Doctor;
using Zexoro.Configuration;

namespace Zexoro.Cli.Commands;

/// <summary><c>zexoro doctor [--config &lt;path&gt;]</c>: checks the configuration and what it refers to.</summary>
internal static class DoctorCommand
{
    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CliEnvironment environment)
    {
        string? configPath = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                stdout.WriteLine(App.Usage);
                return ExitCodes.Success;
            }

            string? value;
            if (arg == "--config")
            {
                if (i + 1 >= args.Count)
                {
                    return App.UsageError(stderr, "doctor: --config needs a path");
                }

                value = args[++i];
            }
            else if (arg.StartsWith("--config=", StringComparison.Ordinal))
            {
                value = arg["--config=".Length..];
            }
            else
            {
                return App.UsageError(stderr, $"doctor: unrecognized argument: {arg}");
            }

            if (configPath is not null || value.Length == 0)
            {
                return App.UsageError(stderr, value.Length == 0 ? "doctor: --config needs a path" : "doctor: --config given more than once");
            }

            configPath = value;
        }

        var displayPath = configPath ?? ZexoroConfig.DefaultFileName;
        var fullPath = Path.GetFullPath(displayPath, environment.WorkingDirectory);
        var report = new DoctorReport();
        DoctorChecks.Run(fullPath, displayPath, environment, report);
        report.Write(stdout);
        return report.ErrorCount > 0 ? ExitCodes.Failure : ExitCodes.Success;
    }
}
