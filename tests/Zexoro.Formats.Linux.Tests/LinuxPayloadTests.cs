namespace Zexoro.Formats.Linux.Tests;

public sealed class LinuxPayloadTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-payload-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Input => Path.Combine(_dir.FullName, "in");

    private void Write(string relative, byte[] content)
    {
        var path = Path.Combine(Input, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, TarItem.Regular);
        }
    }

    private Dictionary<string, TarItem> Read(string main = "notes", string? exclude = null) =>
        LinuxPayload.Read(Input, "Notes/app", main, exclude).ToDictionary(i => i.Name);

    [Fact]
    public void Read_NormalizesModes()
    {
        Write("notes", "not even ELF"u8.ToArray());
        Write("lib/libnative.so", TarFiles.Elf(62));
        Write("bin/helper.sh", "#!/bin/sh\necho hi\n"u8.ToArray());
        Write("data/readme.txt", "hello"u8.ToArray());
        Directory.CreateDirectory(Path.Combine(Input, "empty"));

        var items = Read();

        Assert.Equal(TarItem.Executable, items["Notes/app/notes"].Mode);
        Assert.Equal(TarItem.Executable, items["Notes/app/lib/libnative.so"].Mode);
        Assert.Equal(TarItem.Executable, items["Notes/app/bin/helper.sh"].Mode);
        Assert.Equal(TarItem.Regular, items["Notes/app/data/readme.txt"].Mode);
        Assert.Equal(TarItemKind.Directory, items["Notes/app/empty"].Kind);
        Assert.Equal(TarItem.Executable, items["Notes/app/data"].Mode);
    }

    [Fact]
    public void Read_ExecuteBitOnUnixHosts_MakesAFileExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows has no execute bits.");
            return;
        }

        Write("notes", TarFiles.Elf(62));
        Write("tool", "binary without a known header"u8.ToArray());
        File.SetUnixFileMode(Path.Combine(Input, "tool"), (UnixFileMode)0x1C0); // 0700

        Assert.Equal(TarItem.Executable, Read()["Notes/app/tool"].Mode);
    }

    [Fact]
    public void Read_SkipsTheExcludedDirectory()
    {
        Write("notes", TarFiles.Elf(62));
        Write("dist/old.tar.gz", [1]);

        var items = Read(exclude: Path.Combine(Input, "dist"));

        Assert.DoesNotContain(items.Keys, k => k.Contains("dist", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_RelativeLinksInside_AreKeptAsLinks()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        Write("notes", TarFiles.Elf(62));
        Write("lib/libfoo.so.1", TarFiles.Elf(62));
        File.CreateSymbolicLink(Path.Combine(Input, "lib", "libfoo.so"), "libfoo.so.1");
        Directory.CreateDirectory(Path.Combine(Input, "share", "real"));
        Write("share/real/a.txt", [1]);
        File.CreateSymbolicLink(Path.Combine(Input, "share", "alias"), "real");
        File.CreateSymbolicLink(Path.Combine(Input, "up"), "lib/../notes");

        var items = Read();

        Assert.Equal(TarItem.Link("Notes/app/lib/libfoo.so", "libfoo.so.1"), items["Notes/app/lib/libfoo.so"]);
        Assert.Equal(TarItemKind.SymbolicLink, items["Notes/app/share/alias"].Kind);
        Assert.DoesNotContain("Notes/app/share/alias/a.txt", items.Keys); // a link to a directory is not followed
        Assert.Equal("lib/../notes", items["Notes/app/up"].LinkTarget);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../outside")]
    [InlineData("lib/../../outside")]
    [InlineData("lib//libfoo.so.1")]
    public void Read_LinkOutsideTheInput_Throws(string target)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        Write("notes", TarFiles.Elf(62));
        Write("lib/libfoo.so.1", [1]);
        File.CreateSymbolicLink(Path.Combine(Input, "bad"), target);

        var error = Assert.Throws<PayloadException>(() => Read());

        Assert.Contains("'bad'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_LinkThroughAnotherLink_Throws()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links needs a privilege on Windows.");
        Write("notes", TarFiles.Elf(62));
        Write("real/a.txt", [1]);
        File.CreateSymbolicLink(Path.Combine(Input, "alias"), "real");
        File.CreateSymbolicLink(Path.Combine(Input, "via"), "alias/a.txt");

        var error = Assert.Throws<PayloadException>(() => Read());

        Assert.Contains("through the link 'alias'", error.Message, StringComparison.Ordinal);
    }
}
