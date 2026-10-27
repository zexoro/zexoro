using System.Diagnostics;
using System.Text;

namespace Zexoro.Formats.Nsis;

/// <summary>An extracted NSIS toolset: <c>Bin/makensis</c> plus the data it needs (stubs, plug-ins, includes, Modern UI).</summary>
public sealed class NsisToolset
{
    /// <summary>Points zexoro at an NSIS directory to use instead of the bundled toolset (for development).</summary>
    public const string DirectoryVariable = "ZEXORO_NSIS_DIR";

    /// <summary>The toolset name in <c>eng/toolsets.json</c> and the cache.</summary>
    public const string Name = "nsis";

    private static readonly string[] RequiredFiles =
    [
        "Stubs/lzma_solid-x86-unicode",
        "Stubs/uninst",
        "Include/MUI2.nsh",
        "Include/LogicLib.nsh",
        "Include/x64.nsh",
        "Plugins/x86-unicode/System.dll",
        "Plugins/x86-unicode/UserInfo.dll",
        "Contrib/Language files/English.nlf",
    ];

    private NsisToolset(string directory, string source)
    {
        Directory = directory;
        Source = source;
        MakensisPath = Path.Combine(directory, "Bin", OperatingSystem.IsWindows() ? "makensis.exe" : "makensis");
    }

    /// <summary>The toolset root (NSISDIR).</summary>
    public string Directory { get; }

    /// <summary>Where the toolset comes from, for messages (for example "bundled NSIS 3.13-zexoro.1").</summary>
    public string Source { get; }

    /// <summary><c>Bin/makensis</c>; makensis takes the parent of its own directory as NSISDIR, so it is always run from there.</summary>
    public string MakensisPath { get; }

    /// <summary>Skips documentation and examples when extracting the bundled archive.</summary>
    public static bool ExtractFilter(string relativePath) =>
        !relativePath.StartsWith("Docs/", StringComparison.Ordinal) && relativePath != "Docs"
        && !relativePath.StartsWith("Examples/", StringComparison.Ordinal) && relativePath != "Examples";

    /// <summary>Uses the NSIS toolset in <paramref name="directory"/>.</summary>
    /// <exception cref="NsisException">The directory does not hold a usable toolset.</exception>
    public static NsisToolset Open(string directory, string source)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var toolset = new NsisToolset(Path.GetFullPath(directory), source);
        if (!File.Exists(toolset.MakensisPath))
        {
            throw new NsisException($"{source}: {toolset.MakensisPath} not found.");
        }

        var missing = RequiredFiles.FirstOrDefault(f => !File.Exists(Path.Combine(toolset.Directory, f.Replace('/', Path.DirectorySeparatorChar))));
        return missing is null
            ? toolset
            : throw new NsisException($"{source}: {missing} not found in {toolset.Directory}; the toolset is incomplete.");
    }

    /// <summary>Runs <c>makensis -VERSION</c> and returns its output, e.g. <c>v3.13</c>.</summary>
    /// <exception cref="NsisException">makensis cannot be started or fails.</exception>
    public string GetVersion()
    {
        var (exitCode, output) = Run(["-VERSION"], Directory, null);
        return exitCode == 0
            ? output.Trim()
            : throw new NsisException($"'{MakensisPath} -VERSION' failed with exit code {exitCode}: {output.Trim()}");
    }

    /// <summary>Runs makensis and returns its exit code and combined output.</summary>
    /// <param name="arguments">The arguments.</param>
    /// <param name="workingDirectory">The working directory.</param>
    /// <param name="environment">Variables to set (or, with a null value, remove) for makensis and the commands it runs.</param>
    /// <exception cref="NsisException">makensis cannot be started.</exception>
    public (int ExitCode, string Output) Run(IEnumerable<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?>? environment)
    {
        var start = new ProcessStartInfo(MakensisPath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (!OperatingSystem.IsWindows())
        {
            // A fixed UTF-8 locale keeps makensis's messages UTF-8 whatever the user's locale is,
            // matching the encoding used to read them.
            start.Environment["LC_ALL"] = "C.UTF-8";
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        var output = new StringBuilder();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new NsisException($"Cannot run {MakensisPath}: {ex.Message}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        lock (output)
        {
            return (process.ExitCode, output.ToString());
        }
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            output.Append(line).Append('\n');
        }
    }
}
