using System.Runtime.Versioning;
using Microsoft.Win32;
using Zexoro.Updates;

namespace Zexoro.Updater.Tests;

/// <summary>The Windows pieces on a real file system and registry; skipped elsewhere.</summary>
public sealed class WindowsTests : IDisposable
{
    private readonly TestInstall _install = new();

    public void Dispose() => _install.Dispose();

    [Fact]
    public void Swap_RollsBackWhenAFileIsInUse()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows file locking");
        _install.WriteStagedInstall("2.0.0");
        var before = _install.Snapshot();

        // An open handle without delete sharing blocks the rename of uninstall.exe, after app was swapped.
        using (new FileStream(Path.Combine(_install.Root, "uninstall.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var swap = FileSwap.Plan(_install.Root);
            var ex = Assert.Throws<SwapException>(() => swap.Apply(FileMover.Instance));
            Assert.True(ex.RolledBack);
            Assert.Contains("uninstall.exe", ex.Message, StringComparison.Ordinal);
        }

        Assert.Equal(before, _install.Snapshot());
    }

    [Fact]
    public void Swap_CannotRenameAnAppDirectoryInUse()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows file locking");
        _install.WriteStagedInstall("2.0.0");
        var before = _install.Snapshot();
        using (new FileStream(Path.Combine(_install.Root, "app", "lib", "data.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var ex = Assert.Throws<SwapException>(() => FileSwap.Plan(_install.Root).Apply(FileMover.Instance));
            Assert.True(ex.RolledBack);
        }

        Assert.Equal(before, _install.Snapshot());
    }

    [Fact]
    public void Swap_RenamesARunningExecutable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows file locking");

        // The handing-off updater keeps running from the root while the copy swaps its file.
        // A running image is mapped with delete sharing; FileShare.Delete stands in for it.
        _install.WriteStagedInstall("2.0.0");
        using (new FileStream(Path.Combine(_install.Root, "zexoro-updater.exe"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            FileSwap.Plan(_install.Root).Apply(FileMover.Instance);
            Assert.Equal("updater 2.0.0", File.ReadAllText(Path.Combine(_install.Root, "zexoro-updater.exe")));
            Assert.Equal([Path.Combine(_install.Root, "zexoro-updater.exe.old")], FileSwap.DeleteOld(_install.Root));
        }

        Assert.Empty(FileSwap.DeleteOld(_install.Root));
    }

    [Theory]
    [InlineData("x64", true)]
    [InlineData("arm64", true)]
    [InlineData("x86", false)]
    public void Registry_UpdatesTheUninstallEntryInTheInstallersView(string arch, bool registry64)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows registry");
        if (OperatingSystem.IsWindows())
        {
            RoundTrip(arch, registry64 ? RegistryView.Registry64 : RegistryView.Registry32);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RoundTrip(string arch, RegistryView view)
    {
        var app = $"com.zexoro.updater-test.{Guid.NewGuid():N}";
        var keyPath = WindowsUninstallRegistry.UninstallKeyPrefix + app;
        var record = UpdateInstallRecord.Parse(System.Text.Encoding.UTF8.GetBytes(
            $$"""{"schema":1,"app":"{{app}}","version":"1.0.0","os":"windows","arch":"{{arch}}","format":"nsis","scope":"user","strategy":"replace"}"""));
        using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
        try
        {
            var registry = new WindowsUninstallRegistry();
            Assert.Null(registry.Read(record));
            using (var key = hive.CreateSubKey(keyPath))
            {
                key.SetValue("DisplayVersion", "1.0.0");
                key.SetValue("EstimatedSize", 10, RegistryValueKind.DWord);
                key.SetValue("DisplayName", "Test");
            }

            Assert.Equal(new UninstallEntry("1.0.0", null, 10), registry.Read(record));
            registry.Write(record, new UninstallEntry("2.0.0", "000020000000000", 3_000_000_000));
            Assert.Equal(new UninstallEntry("2.0.0", "000020000000000", 3_000_000_000), registry.Read(record));
            using (var key = hive.OpenSubKey(keyPath)!)
            {
                Assert.Equal("Test", key.GetValue("DisplayName"));
                Assert.Equal(RegistryValueKind.DWord, key.GetValueKind("EstimatedSize"));
            }

            registry.Write(record, new UninstallEntry("1.0.0", null, null));
            Assert.Equal(new UninstallEntry("1.0.0", null, null), registry.Read(record));
        }
        finally
        {
            hive.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void Processes_TreatAnExitedProcessAsGone()
    {
        var host = new SystemProcessHost();
        Assert.True(host.WaitForExit(int.MaxValue, TimeSpan.FromSeconds(1)));
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        Assert.False(host.WaitForExit(current.Id, TimeSpan.FromMilliseconds(50)));
    }
}
