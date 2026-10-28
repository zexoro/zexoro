using System.Diagnostics;

namespace Zexoro.Formats.MacOS;

/// <summary>The outcome of running a tool.</summary>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="Output">Everything written to standard output.</param>
/// <param name="Error">Everything written to standard error.</param>
public sealed record ToolResult(int ExitCode, string Output, string Error);

/// <summary>
/// Runs the macOS tools zexoro uses but does not ship (<c>codesign</c>, <c>xcrun notarytool</c>,
/// <c>xcrun stapler</c>; docs/design/0006-desktop-formats.md section 3). Tests substitute a fake
/// that records the command lines, since signing with a real identity and notarizing need an Apple
/// Developer account that CI does not have.
/// </summary>
public interface IMacToolRunner
{
    /// <summary>Runs <paramref name="fileName"/> with <paramref name="arguments"/> and waits for it.</summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The tool cannot be started (not installed).</exception>
    ToolResult Run(string fileName, IReadOnlyList<string> arguments);
}

/// <summary>Runs tools as child processes, capturing their output.</summary>
public sealed class ProcessToolRunner : IMacToolRunner
{
    public static ProcessToolRunner Instance { get; } = new();

    public ToolResult Run(string fileName, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{fileName} did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new ToolResult(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }
}
