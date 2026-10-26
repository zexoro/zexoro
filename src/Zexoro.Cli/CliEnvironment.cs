namespace Zexoro.Cli;

/// <summary>What a command reads from its surroundings, injectable for tests.</summary>
/// <param name="WorkingDirectory">The directory relative paths on the command line are resolved against.</param>
/// <param name="GetVariable">Looks up an environment variable; returns null when it is not set.</param>
internal sealed record CliEnvironment(string WorkingDirectory, Func<string, string?> GetVariable)
{
    /// <summary>The real process environment.</summary>
    public static CliEnvironment Process => new(Environment.CurrentDirectory, Environment.GetEnvironmentVariable);
}
