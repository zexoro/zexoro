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

    /// <summary>Writes one fake updater per architecture into <paramref name="directory"/> and returns it.</summary>
    public static string Create(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var architecture in UpdaterBinary.Architectures)
        {
            File.WriteAllBytes(Path.Combine(directory, UpdaterBinary.FileNameFor(architecture)), Pe(Machine(architecture)));
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
