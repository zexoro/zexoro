using System.Security.Cryptography;
using Zexoro.Cli.Toolsets;
using Zexoro.Formats.Nsis;
using Zexoro.Platforms;

namespace Zexoro.Cli.Tests;

/// <summary>Minimal PE files that stand in for zexoro-updater-&lt;rid&gt;.exe.</summary>
internal static class UpdaterFiles
{
    public static byte[] Pe(ushort machine)
    {
        var bytes = new byte[128];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        bytes[0x3C] = 64;
        "PE\0\0"u8.CopyTo(bytes.AsSpan(64));
        BitConverter.TryWriteBytes(bytes.AsSpan(68), machine);
        return bytes;
    }

    public static ushort Machine(Architecture architecture) =>
        architecture == Architecture.X86 ? (ushort)0x014C : architecture == Architecture.X64 ? (ushort)0x8664 : (ushort)0xAA64;

    /// <summary>A minimal 64-bit little-endian ELF executable header.</summary>
    public static byte[] Elf(ushort machine)
    {
        var bytes = new byte[64];
        "\u007FELF"u8.CopyTo(bytes);
        bytes[4] = 2;
        bytes[5] = 1;
        bytes[6] = 1;
        BitConverter.TryWriteBytes(bytes.AsSpan(16), (ushort)3);
        BitConverter.TryWriteBytes(bytes.AsSpan(18), machine);
        return bytes;
    }

    /// <summary>A minimal thin 64-bit Mach-O header.</summary>
    public static byte[] MachO(uint cpuType)
    {
        var bytes = new byte[32];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), 0xFEEDFACFu);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), cpuType);
        return bytes;
    }

    /// <summary>A fake executable whose header matches <paramref name="runtime"/>.</summary>
    public static byte[] For(UpdaterRuntime runtime)
    {
        var platform = runtime.Platform;
        var arm64 = platform.Architecture == Architecture.Arm64;
        return platform.OperatingSystem == OperatingSystemFamily.Windows ? Pe(Machine(platform.Architecture))
            : platform.OperatingSystem == OperatingSystemFamily.Linux ? Elf(arm64 ? (ushort)183 : (ushort)62)
            : MachO(arm64 ? 0x0100000Cu : 0x01000007u);
    }

    /// <summary>Writes one fake updater per runtime identifier into <paramref name="directory"/> and returns it.</summary>
    public static string Create(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var runtime in UpdaterRuntime.All)
        {
            File.WriteAllBytes(Path.Combine(directory, runtime.DirectoryFileName), For(runtime));
        }

        return directory;
    }
}

public sealed class UpdaterTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-updaters-");
    private readonly Dictionary<string, string> _variables = [];

    public void Dispose() => _dir.Delete(recursive: true);

    private CliEnvironment Environment => new(_dir.FullName, name => _variables.GetValueOrDefault(name));

    [Fact]
    public void Resolve_UsesTheOverrideDirectory()
    {
        UpdaterFiles.Create(Path.Combine(_dir.FullName, "updaters"));
        _variables[UpdaterBinary.DirectoryVariable] = "updaters";
        foreach (var architecture in UpdaterBinary.Architectures)
        {
            var updater = BundledUpdaters.Resolve(architecture, Environment);
            Assert.Equal(architecture, updater.Architecture);
            Assert.Contains("ZEXORO_UPDATER_DIR", updater.Source, StringComparison.Ordinal);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(UpdaterFiles.Pe(UpdaterFiles.Machine(architecture)))), updater.Sha256);
        }
    }

    [Fact]
    public void Resolve_UsesTheOverrideDirectoryForEveryRuntime()
    {
        _variables[UpdaterBinary.DirectoryVariable] = UpdaterFiles.Create(Path.Combine(_dir.FullName, "updaters"));
        foreach (var runtime in UpdaterRuntime.All)
        {
            var updater = BundledUpdaters.Resolve(runtime, Environment);
            Assert.Equal(runtime, updater.Runtime);
            Assert.Contains(runtime.DirectoryFileName, updater.Source, StringComparison.Ordinal);
            using var stream = updater.Open();
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(UpdaterFiles.For(runtime))), Convert.ToHexStringLower(SHA256.HashData(stream)));
        }
    }

    [Fact]
    public void Resolve_RejectsAnUpdaterForAnotherOperatingSystem()
    {
        var directory = UpdaterFiles.Create(Path.Combine(_dir.FullName, "updaters"));
        File.Copy(Path.Combine(directory, "zexoro-updater-osx-arm64"), Path.Combine(directory, "zexoro-updater-linux-musl-arm64"), overwrite: true);
        _variables[UpdaterBinary.DirectoryVariable] = directory;
        var ex = Assert.Throws<UpdaterUnavailableException>(() => BundledUpdaters.Resolve(UpdaterRuntime.LinuxMuslArm64, Environment));
        Assert.Contains("not a Linux arm64 executable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindEmbedded_ReturnsEveryRuntimeOrNone()
    {
        // Test builds embed none (no ZexoroUpdaterDir), unless CI builds them with one; then all of them.
        var embedded = UpdaterRuntime.All.Select(BundledUpdaters.FindEmbedded).ToList();
        Assert.True(embedded.TrueForAll(u => u is null) || embedded.TrueForAll(u => u is not null));
        foreach (var updater in embedded.OfType<AvailableUpdater>())
        {
            using var stream = updater.Open();
            var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            Assert.Equal(updater.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray())));
            bytes.Position = 0;
            Assert.Equal(updater.Runtime.Platform, ExecutableInspector.Inspect(bytes).Target);
        }
    }

    [Fact]
    public void Resolve_RejectsAnUpdaterForAnotherArchitecture()
    {
        var directory = UpdaterFiles.Create(Path.Combine(_dir.FullName, "updaters"));
        File.Copy(Path.Combine(directory, "zexoro-updater-win-x86.exe"), Path.Combine(directory, "zexoro-updater-win-x64.exe"), overwrite: true);
        _variables[UpdaterBinary.DirectoryVariable] = directory;
        var ex = Assert.Throws<NsisException>(() => BundledUpdaters.Resolve(Architecture.X64, Environment));
        Assert.Contains("not a Windows x64 executable", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ExplainsAMissingUpdater()
    {
        _variables[UpdaterBinary.DirectoryVariable] = _dir.FullName;
        Assert.Contains("does not exist", Assert.Throws<NsisException>(() => BundledUpdaters.Resolve(Architecture.Arm64, Environment)).Message, StringComparison.Ordinal);
        Assert.Throws<NsisException>(() => BundledUpdaters.Resolve(Architecture.Arm, Environment));
    }

    [Fact]
    public void Resolve_FallsBackToTheEmbeddedUpdaters()
    {
        // Test builds embed none (no ZexoroUpdaterDir), unless CI builds them with one.
        var embedded = BundledUpdaters.FindEmbedded(Architecture.X64);
        if (embedded is null)
        {
            var ex = Assert.Throws<NsisException>(() => BundledUpdaters.Resolve(Architecture.X64, Environment));
            Assert.Contains("no embedded updater for win-x64", ex.Message, StringComparison.Ordinal);
        }
        else
        {
            var path = BundledUpdaters.Extract(Architecture.X64, Environment, Path.Combine(_dir.FullName, "out"));
            Assert.Equal(Architecture.X64, PeMachine.Read(path));
        }
    }

    [Fact]
    public void Extract_WritesTheUpdaterForTheArchitecture()
    {
        _variables[UpdaterBinary.DirectoryVariable] = UpdaterFiles.Create(Path.Combine(_dir.FullName, "updaters"));
        var path = BundledUpdaters.Extract(Architecture.Arm64, Environment, Path.Combine(_dir.FullName, "staging"));
        Assert.Equal(Path.Combine(_dir.FullName, "staging", "zexoro-updater.exe"), path);
        Assert.Equal(UpdaterFiles.Pe(0xAA64), File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteTo_RejectsAHashMismatch()
    {
        var bytes = UpdaterFiles.Pe(0x8664);
        var updater = new UpdaterBinary(Architecture.X64, "test", new string('0', 64), () => new MemoryStream(bytes));
        var path = Path.Combine(_dir.FullName, "zexoro-updater.exe");
        Assert.Contains("SHA-256", Assert.Throws<NsisException>(() => updater.WriteTo(path)).Message, StringComparison.Ordinal);
        Assert.Empty(_dir.EnumerateFiles());
    }

    [Fact]
    public void WriteTo_RejectsAnotherArchitecture()
    {
        var bytes = UpdaterFiles.Pe(0x014C);
        var updater = new UpdaterBinary(Architecture.X64, "test", Convert.ToHexStringLower(SHA256.HashData(bytes)), () => new MemoryStream(bytes));
        Assert.Throws<NsisException>(() => updater.WriteTo(Path.Combine(_dir.FullName, "zexoro-updater.exe")));
        Assert.Empty(_dir.EnumerateFiles());
    }
}
