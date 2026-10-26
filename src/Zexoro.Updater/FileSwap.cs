namespace Zexoro.Updater;

/// <summary>One rename of the swap.</summary>
internal sealed record RenameStep(string Source, string Destination);

/// <summary>The swap failed. <see cref="RolledBack"/> tells whether every completed rename was undone.</summary>
internal sealed class SwapException(string message, bool rolledBack, Exception innerException)
    : Exception(message, innerException)
{
    public bool RolledBack { get; } = rolledBack;
}

/// <summary>
/// Swaps a staged install into place by renames (docs/design/0005-nsis-updates.md, replace step 4):
/// <c>app</c> → <c>app.old</c>, <c>app.new</c> → <c>app</c>, then for each staged root file
/// <c>F.new</c>: <c>F</c> → <c>F.old</c>, <c>F.new</c> → <c>F</c>. A failure renames everything back in
/// reverse order.
/// </summary>
internal sealed class FileSwap
{
    public const string AppDirectoryName = "app";
    public const string NewSuffix = ".new";
    public const string OldSuffix = ".old";

    private readonly List<RenameStep> _done = [];

    private FileSwap(string root, IReadOnlyList<RenameStep> steps)
    {
        Root = root;
        Steps = steps;
    }

    public string Root { get; }

    public IReadOnlyList<RenameStep> Steps { get; }

    /// <summary>
    /// Deletes what an interrupted run may have left in <paramref name="root"/>: <c>app.new</c>,
    /// <c>app.old</c> and every <c>*.new</c> and <c>*.old</c> file.
    /// </summary>
    /// <exception cref="IOException">Something cannot be deleted (it is in use).</exception>
    /// <exception cref="UnauthorizedAccessException">Something cannot be deleted.</exception>
    public static void DeleteLeftovers(string root)
    {
        foreach (var name in new[] { AppDirectoryName + NewSuffix, AppDirectoryName + OldSuffix })
        {
            var path = Path.Combine(root, name);
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        foreach (var file in StagedRootFiles(root, NewSuffix).Concat(StagedRootFiles(root, OldSuffix)))
        {
            File.Delete(file);
        }
    }

    /// <summary>Deletes the <c>.old</c> items after a successful swap; returns those still in use.</summary>
    public static IReadOnlyList<string> DeleteOld(string root)
    {
        var left = new List<string>();
        var app = Path.Combine(root, AppDirectoryName + OldSuffix);
        if (Directory.Exists(app))
        {
            try
            {
                Directory.Delete(app, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left.Add(app);
            }
        }

        foreach (var file in StagedRootFiles(root, OldSuffix))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left.Add(file);
            }
        }

        return left;
    }

    /// <summary>Plans the swap of what the installer staged in <paramref name="root"/>.</summary>
    /// <exception cref="InvalidOperationException"><c>app</c> or <c>app.new</c> is missing.</exception>
    public static FileSwap Plan(string root)
    {
        var app = Path.Combine(root, AppDirectoryName);
        var stagedApp = app + NewSuffix;
        if (!Directory.Exists(stagedApp))
        {
            throw new InvalidOperationException($"the installer staged no {stagedApp}");
        }

        if (!Directory.Exists(app))
        {
            throw new InvalidOperationException($"{app} does not exist");
        }

        var steps = new List<RenameStep> { new(app, app + OldSuffix), new(stagedApp, app) };
        foreach (var staged in StagedRootFiles(root, NewSuffix).Order(StringComparer.Ordinal))
        {
            var target = staged[..^NewSuffix.Length];
            if (File.Exists(target))
            {
                steps.Add(new RenameStep(target, target + OldSuffix));
            }

            steps.Add(new RenameStep(staged, target));
        }

        return new FileSwap(root, steps);
    }

    /// <summary>Runs every rename; on a failure renames back what was done.</summary>
    /// <exception cref="SwapException">A rename failed (and the rollback may have failed too).</exception>
    public void Apply(IFileMover mover)
    {
        ArgumentNullException.ThrowIfNull(mover);
        foreach (var step in Steps)
        {
            try
            {
                mover.Move(step.Source, step.Destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var message = $"cannot rename {step.Source} to {step.Destination}: {ex.Message}";
                try
                {
                    Rollback(mover);
                }
                catch (SwapException rollback)
                {
                    throw new SwapException($"{message}; {rollback.Message}", rolledBack: false, ex);
                }

                throw new SwapException(message, rolledBack: true, ex);
            }

            _done.Add(step);
        }
    }

    /// <summary>Renames back every completed step, newest first.</summary>
    /// <exception cref="SwapException">A rename back failed; the install is left half swapped.</exception>
    public void Rollback(IFileMover mover)
    {
        ArgumentNullException.ThrowIfNull(mover);
        for (var i = _done.Count - 1; i >= 0; i--)
        {
            var step = _done[i];
            try
            {
                mover.Move(step.Destination, step.Source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SwapException($"rolling back failed: cannot rename {step.Destination} back to {step.Source}: {ex.Message}", rolledBack: false, ex);
            }

            _done.RemoveAt(i);
        }
    }

    private static IEnumerable<string> StagedRootFiles(string root, string suffix) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*" + suffix).Where(f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            : [];
}
