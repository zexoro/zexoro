namespace Zexoro.Cli;

/// <summary>What a command reads from its surroundings, injectable for tests.</summary>
/// <param name="WorkingDirectory">The directory relative paths on the command line are resolved against.</param>
/// <param name="GetVariable">Looks up an environment variable; returns null when it is not set.</param>
internal sealed record CliEnvironment(string WorkingDirectory, Func<string, string?> GetVariable)
{
    /// <summary>The real process environment.</summary>
    public static CliEnvironment Process => new(Environment.CurrentDirectory, Environment.GetEnvironmentVariable);

    /// <summary>
    /// The command line that runs this zexoro again, for tools that call back into it (makensis
    /// signing the uninstaller and the installer through <c>zexoro sign-file</c>).
    /// </summary>
    public IReadOnlyList<string> ZexoroCommand { get; init; } = DetectZexoroCommand();

    private static string[] DetectZexoroCommand()
    {
        var process = Environment.ProcessPath ?? "zexoro";
        var name = Path.GetFileNameWithoutExtension(process);

        // Under 'dotnet zexoro.dll' the process is the dotnet host; Native AOT and apphost builds run directly.
        return string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase)
            ? [process, "exec", Path.Combine(AppContext.BaseDirectory, "zexoro.dll")]
            : [process];
    }
}
