namespace Zexoro.Cli.Commands;

/// <summary>Parses <c>--name value</c> and <c>--name=value</c> options, each given at most once.</summary>
internal static class CommandOptions
{
    /// <summary>
    /// Returns the values by option name, or null after writing a usage error (whose exit code is in
    /// <paramref name="exitCode"/>); <c>-h</c>/<c>--help</c> writes the usage and returns null with exit code 0.
    /// </summary>
    public static Dictionary<string, string>? Parse(
        string command, IReadOnlyList<string> args, string[] names, TextWriter stdout, TextWriter stderr, out int exitCode)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        exitCode = ExitCodes.Success;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                stdout.WriteLine(App.Usage);
                return null;
            }

            var equals = arg.IndexOf('=', StringComparison.Ordinal);
            var name = equals > 0 ? arg[..equals] : arg;
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                exitCode = App.UsageError(stderr, $"{command}: unrecognized argument: {arg}");
                return null;
            }

            string value;
            if (equals > 0)
            {
                value = arg[(equals + 1)..];
            }
            else if (i + 1 < args.Count)
            {
                value = args[++i];
            }
            else
            {
                value = string.Empty;
            }

            if (value.Length == 0)
            {
                exitCode = App.UsageError(stderr, $"{command}: {name} needs a value");
                return null;
            }

            if (!values.TryAdd(name, value))
            {
                exitCode = App.UsageError(stderr, $"{command}: {name} given more than once");
                return null;
            }
        }

        return values;
    }
}
