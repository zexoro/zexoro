namespace Zexoro.Cli.Doctor;

internal enum Severity
{
    Ok,
    Warning,
    Error,
}

internal sealed record Finding(Severity Severity, string Message);

/// <summary>The findings of <c>zexoro doctor</c>, in the order they were made.</summary>
internal sealed class DoctorReport
{
    private readonly List<Finding> _findings = [];

    public IReadOnlyList<Finding> Findings => _findings;

    public int ErrorCount => _findings.Count(f => f.Severity == Severity.Error);

    public int WarningCount => _findings.Count(f => f.Severity == Severity.Warning);

    public void Ok(string message) => _findings.Add(new Finding(Severity.Ok, message));

    public void Warning(string message) => _findings.Add(new Finding(Severity.Warning, message));

    public void Error(string message) => _findings.Add(new Finding(Severity.Error, message));

    public void Write(TextWriter writer)
    {
        foreach (var finding in _findings)
        {
            var label = finding.Severity switch
            {
                Severity.Ok => "ok",
                Severity.Warning => "warning",
                _ => "error",
            };
            writer.WriteLine($"{label,-8} {finding.Message}");
        }

        writer.WriteLine();
        writer.WriteLine(ErrorCount == 0 && WarningCount == 0
            ? "No problems found."
            : $"{Count(ErrorCount, "error")}, {Count(WarningCount, "warning")}.");
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
