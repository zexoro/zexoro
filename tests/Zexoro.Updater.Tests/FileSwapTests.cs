namespace Zexoro.Updater.Tests;

public sealed class FileSwapTests : IDisposable
{
    private readonly TestInstall _install = new();

    public void Dispose() => _install.Dispose();

    private string Root => _install.Root;

    [Fact]
    public void Plan_RenamesAppThenEachStagedRootFile()
    {
        _install.WriteStagedInstall("2.0.0");
        var steps = FileSwap.Plan(Root).Steps.Select(s => $"{Path.GetFileName(s.Source)}>{Path.GetFileName(s.Destination)}");
        Assert.Equal(
            [
                "app>app.old", "app.new>app",
                "uninstall.exe>uninstall.exe.old", "uninstall.exe.new>uninstall.exe",
                "zexoro-install.json>zexoro-install.json.old", "zexoro-install.json.new>zexoro-install.json",
                "zexoro-updater.exe>zexoro-updater.exe.old", "zexoro-updater.exe.new>zexoro-updater.exe",
            ],
            steps);
    }

    [Fact]
    public void Plan_NeedsAStagedAppDirectory()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => FileSwap.Plan(Root));
        Assert.Contains("app.new", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_SwapsEverything()
    {
        _install.WriteStagedInstall("2.0.0");
        FileSwap.Plan(Root).Apply(FileMover.Instance);
        Assert.Equal("updater 2.0.0", File.ReadAllText(Path.Combine(Root, "zexoro-updater.exe")));
        Assert.Equal("updater 1.0.0", File.ReadAllText(Path.Combine(Root, "zexoro-updater.exe.old")));
        Assert.Equal("data 2.0.0", File.ReadAllText(Path.Combine(Root, "app", "lib", "data.txt")));
        Assert.Equal("data 1.0.0", File.ReadAllText(Path.Combine(Root, "app.old", "lib", "data.txt")));
        Assert.Empty(FileSwap.DeleteOld(Root));
        Assert.DoesNotContain(_install.Snapshot().Keys, k => k.Contains(".old", StringComparison.Ordinal) || k.Contains(".new", StringComparison.Ordinal));
    }

    public static TheoryData<int> EveryStep => [.. Enumerable.Range(0, 8)];

    [Theory]
    [MemberData(nameof(EveryStep))]
    public void Apply_RollsBackAFailureAtAnyStep(int failingStep)
    {
        _install.WriteStagedInstall("2.0.0");
        var before = _install.Snapshot();
        var swap = FileSwap.Plan(Root);
        Assert.Equal(8, swap.Steps.Count);
        var ex = Assert.Throws<SwapException>(() => swap.Apply(new FailingMover(failAt: failingStep)));
        Assert.True(ex.RolledBack);
        Assert.Equal(before, _install.Snapshot());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void Apply_ReportsAFailedRollback(int failingStep)
    {
        _install.WriteStagedInstall("2.0.0");
        var swap = FileSwap.Plan(Root);

        // The failing step and then the first rename back fail.
        var ex = Assert.Throws<SwapException>(() => swap.Apply(new FailingMover(failAt: failingStep, alsoFailAt: failingStep + 1)));
        Assert.False(ex.RolledBack);
        Assert.Contains("rolling back failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback_UndoesACompletedSwap()
    {
        _install.WriteStagedInstall("2.0.0");
        var before = _install.Snapshot();
        var swap = FileSwap.Plan(Root);
        swap.Apply(FileMover.Instance);
        Assert.NotEqual(before, _install.Snapshot());
        swap.Rollback(FileMover.Instance);
        Assert.Equal(before, _install.Snapshot());
    }

    [Fact]
    public void DeleteLeftovers_RemovesNewAndOldItemsOnly()
    {
        _install.WriteStagedInstall("2.0.0");
        Directory.CreateDirectory(Path.Combine(Root, "app.old", "x"));
        File.WriteAllText(Path.Combine(Root, "uninstall.exe.old"), "old");
        File.WriteAllText(Path.Combine(Root, "notes.newer"), "kept");
        FileSwap.DeleteLeftovers(Root);
        Assert.Equal(["app/Notes.exe", "app/lib/data.txt", "notes.newer", "uninstall.exe", "zexoro-install.json", "zexoro-updater.exe"], _install.Snapshot().Keys);
    }
}

/// <summary>Real renames, except that call number <c>failAt</c> (and <c>alsoFailAt</c>) throws.</summary>
internal sealed class FailingMover(int failAt, int alsoFailAt = -1) : IFileMover
{
    private int _calls;

    public void Move(string source, string destination)
    {
        var call = _calls++;
        if (call == failAt || call == alsoFailAt)
        {
            throw new IOException($"injected failure renaming {Path.GetFileName(source)}");
        }

        FileMover.Instance.Move(source, destination);
    }
}
