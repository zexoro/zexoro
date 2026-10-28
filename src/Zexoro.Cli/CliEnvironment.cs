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

    /// <summary>Whether this zexoro runs on macOS, the only host that can pack macOS formats (codesign).</summary>
    public bool IsMacOSHost { get; init; } = OperatingSystem.IsMacOS();

    /// <summary>Runs macOS tools (codesign, xcrun); tests substitute a fake.</summary>
    public Formats.MacOS.IMacToolRunner MacTools { get; init; } = Formats.MacOS.ProcessToolRunner.Instance;

    /// <summary>The clock, for timestamps such as a feed's <c>published</c>.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

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
