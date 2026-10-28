namespace Zexoro.Formats.MacOS.Tests;

/// <summary>Records tool invocations and answers them from a handler (default: success, no output).</summary>
internal sealed class FakeTools(Func<string, IReadOnlyList<string>, ToolResult>? handler = null) : IMacToolRunner
{
    public List<(string File, List<string> Arguments)> Calls { get; } = [];

    public ToolResult Run(string fileName, IReadOnlyList<string> arguments)
    {
        Calls.Add((fileName, arguments.ToList()));
        return handler?.Invoke(fileName, arguments) ?? new ToolResult(0, string.Empty, string.Empty);
    }
}
