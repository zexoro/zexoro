using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Zexoro.Updates;

namespace Zexoro.Updater;

/// <summary>The process operations the updater needs; replaced by fakes in tests.</summary>
internal interface IProcessHost
{
    /// <summary>Waits for process <paramref name="processId"/> to exit; true when it has exited or never existed.</summary>
    bool WaitForExit(int processId, TimeSpan timeout);

    /// <summary>Runs a program with the updater's standard handles and waits for it; returns its exit code.</summary>
    int RunAndWait(string path, IReadOnlyList<string> arguments, string workingDirectory);

    /// <summary>
    /// Starts a program through <c>ShellExecute</c>, so a <c>requireAdministrator</c> manifest raises UAC,
    /// and waits for it. Returns its exit code, or null when the user declined elevation.
    /// </summary>
    int? ShellExecuteAndWait(string path, string arguments, string workingDirectory);

    /// <summary>Starts a program without waiting for it.</summary>
    void Start(string path, IReadOnlyList<string> arguments, string workingDirectory);
}

/// <summary>The values of an uninstall entry that the updater maintains for the replace strategy.</summary>
/// <param name="DisplayVersion">The <c>DisplayVersion</c> string.</param>
/// <param name="VersionOrder">The <c>ZexoroVersionOrder</c> string (see <c>NsisVersionOrder</c>).</param>
/// <param name="EstimatedSizeKb">The <c>EstimatedSize</c> DWORD, in KiB.</param>
internal sealed record UninstallEntry(string? DisplayVersion, string? VersionOrder, uint? EstimatedSizeKb);

/// <summary>The install's uninstall registry entry; replaced by a fake in tests.</summary>
internal interface IUninstallRegistry
{
    /// <summary>Returns the entry of the install <paramref name="record"/> describes, or null when there is none.</summary>
    UninstallEntry? Read(UpdateInstallRecord record);

    /// <summary>Writes the three values; a null value is deleted.</summary>
    void Write(UpdateInstallRecord record, UninstallEntry entry);
}

/// <summary>Renames a file or directory; replaced in tests to inject failures.</summary>
internal interface IFileMover
{
    void Move(string source, string destination);
}

internal sealed class FileMover : IFileMover
{
    public static FileMover Instance { get; } = new();

    public void Move(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }
}

/// <summary>The real processes.</summary>
internal sealed class SystemProcessHost : IProcessHost
{
    /// <summary>ERROR_CANCELLED: the user declined the UAC prompt.</summary>
    private const int ErrorCancelled = 1223;

    public bool WaitForExit(int processId, TimeSpan timeout)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            try
            {
                return process.WaitForExit(timeout);
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int RunAndWait(string path, IReadOnlyList<string> arguments, string workingDirectory)
    {
        using var process = Process.Start(StartInfo(path, arguments, workingDirectory))
            ?? throw new InvalidOperationException($"cannot start {path}");
        process.WaitForExit();
        return process.ExitCode;
    }

    public int? ShellExecuteAndWait(string path, string arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(path, arguments) { UseShellExecute = true, WorkingDirectory = workingDirectory };
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException($"cannot start {path}");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return null;
        }
    }

    public void Start(string path, IReadOnlyList<string> arguments, string workingDirectory)
    {
        using var process = Process.Start(StartInfo(path, arguments, workingDirectory));
    }

    private static ProcessStartInfo StartInfo(string path, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = workingDirectory };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }
}

/// <summary>
/// The uninstall entry under <c>HKCU</c> (per-user) or <c>HKLM</c> (per-machine), in the registry
/// view the installer used: 64-bit for x64 and arm64 installs (<c>SetRegView 64</c>), 32-bit for x86.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsUninstallRegistry : IUninstallRegistry
{
    internal const string UninstallKeyPrefix = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";

    public UninstallEntry? Read(UpdateInstallRecord record)
    {
        using var hive = OpenHive(record);
        using var key = hive.OpenSubKey(UninstallKeyPrefix + record.App);
        if (key is null)
        {
            return null;
        }

        return new UninstallEntry(
            key.GetValue("DisplayVersion") as string,
            key.GetValue(Formats.Nsis.NsisVersionOrder.RegistryValueName) as string,
            key.GetValue("EstimatedSize") is int size ? unchecked((uint)size) : null);
    }

    public void Write(UpdateInstallRecord record, UninstallEntry entry)
    {
        using var hive = OpenHive(record);
        using var key = hive.OpenSubKey(UninstallKeyPrefix + record.App, writable: true)
            ?? throw new IOException($"the uninstall entry {UninstallKeyPrefix}{record.App} does not exist");
        Set(key, "DisplayVersion", entry.DisplayVersion, RegistryValueKind.String);
        Set(key, Formats.Nsis.NsisVersionOrder.RegistryValueName, entry.VersionOrder, RegistryValueKind.String);
        Set(key, "EstimatedSize", entry.EstimatedSizeKb is { } size ? unchecked((int)size) : null, RegistryValueKind.DWord);
    }

    private static void Set(RegistryKey key, string name, object? value, RegistryValueKind kind)
    {
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(name, value, kind);
        }
    }

    private static RegistryKey OpenHive(UpdateInstallRecord record) => RegistryKey.OpenBaseKey(
        record.Target.Scope == "machine" ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
        record.Target.Arch is "x64" or "arm64" ? RegistryView.Registry64 : RegistryView.Registry32);
}

/// <summary>Stands in for the registry where there is none; the updater only updates Windows installs.</summary>
internal sealed class UnsupportedRegistry : IUninstallRegistry
{
    public UninstallEntry? Read(UpdateInstallRecord record) => throw new PlatformNotSupportedException("uninstall entries exist only on Windows");

    public void Write(UpdateInstallRecord record, UninstallEntry entry) => throw new PlatformNotSupportedException("uninstall entries exist only on Windows");
}
