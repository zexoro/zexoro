using Zexoro.Formats.Nsis;
using Zexoro.Updates;

namespace Zexoro.Updater;

/// <summary>The options of <c>apply</c>.</summary>
/// <param name="WaitPid">The process to wait for before touching the install.</param>
/// <param name="Restart">Start the application when done.</param>
/// <param name="Json">Report as JSON (passed on to the copy).</param>
/// <param name="FromRoot">Set when running as the temporary copy: the install root to update.</param>
internal sealed record ApplyOptions(int? WaitPid, bool Restart, bool Json, string? FromRoot);

/// <summary>
/// <c>apply</c> (docs/design/0005-nsis-updates.md section 3). The updater in the install root checks
/// the staged installer, waits for the application, copies itself to <c>%TEMP%</c> (<c>$TMPDIR</c>
/// on Unix, in a directory only the user can enter) and hands off to that copy
/// (<c>apply --from-root</c>), which runs the installer: in stage mode followed by a rename swap for
/// <c>replace</c>, or through <c>ShellExecute</c> for <c>reinstall</c>. A portable archive
/// (<c>replace</c>, format <c>zip</c> or <c>tar.gz</c>) is extracted instead of run
/// (docs/design/0006-desktop-formats.md).
/// </summary>
internal sealed class ApplyCommand(UpdaterEnvironment environment, TextWriter log)
{
    /// <summary>The updater's file name in a Windows install (NSIS, zip).</summary>
    public const string UpdaterFileName = "zexoro-updater.exe";
    public const string CopyDirectoryPrefix = "zexoro-updater-";
    public const string ReplaceStrategy = "replace";
    public const string ReinstallStrategy = "reinstall";

    /// <summary>The record format of a portable zip, updated by extracting the next archive (docs/design/0006-desktop-formats.md).</summary>
    public const string ZipFormat = "zip";

    /// <summary>
    /// The record and feed format of a portable tar.gz (Linux), updated like a zip but keeping Unix
    /// modes and symbolic links. Not <c>tar.gz</c>: feed tokens allow no '.', and updaters up to
    /// 0.2.0 reject a whole feed with such a token, which would stop the updates of every other
    /// platform sharing the feed.
    /// </summary>
    public const string TarGzFormat = "tar-gz";

    /// <summary>The record and feed format of a macOS bundle (zip or dmg); its update payload is always a zip.</summary>
    public const string AppFormat = "app";

    /// <summary>The strategy of macOS bundles. Applying it comes with stage 5 step 10; until then apply exits 7.</summary>
    public const string BundleStrategy = "bundle";

    /// <summary>On Unix, copies left in the shared temporary directory are removed only once they are this old.</summary>
    internal static readonly TimeSpan UnixCopyMaxAge = TimeSpan.FromHours(1);

    /// <summary>The temporary copy's directory and file: readable, writable and executable by the user only.</summary>
    private const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Exit code of an installer that found a newer version installed (NsisScript).</summary>
    public const int InstallerNewerVersionInstalled = 3;

    public UpdaterResult Run(ApplyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var root = Path.GetFullPath(options.FromRoot ?? environment.DefaultRoot);
        UpdateInstallRecord record;
        try
        {
            record = UpdateInstallRecord.Load(InstallLayout.RecordPath(root));
        }
        catch (UpdateException ex)
        {
            return UpdaterResult.Fail(UpdaterExitCodes.For(ex.Kind), ex.Message);
        }

        if (record.Strategy == Notify.Strategy)
        {
            // The package manager owns the install: nothing is touched, not even the updater's state.
            return Notify.ManualUpdate(record, "apply");
        }

        var state = new UpdaterState(environment.StateBase, record.App);
        var result = options.FromRoot is null ? HandOff(root, record, state, options) : Apply(root, record, state, options);
        if (!result.Relayed)
        {
            try
            {
                UpdaterState.WriteAtomically(state.LastApplyFile, result.ToJson());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.WriteLine($"warning: cannot write {state.LastApplyFile}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>Runs in the install root: checks, waits, copies itself to %TEMP% and runs the copy.</summary>
    private UpdaterResult HandOff(string root, UpdateInstallRecord record, UpdaterState state, ApplyOptions options)
    {
        var installed = record.Version.ToString();
        if (record.Strategy == BundleStrategy)
        {
            // Nothing is touched: swapping a signed bundle needs the code-signing checks of the bundle strategy.
            return UpdaterResult.Fail(
                UpdaterExitCodes.ManualUpdateNeeded,
                $"this updater cannot apply macOS bundle updates yet; install {record.App} from the new zip manually",
                installed);
        }

        if (record.Strategy is not (ReplaceStrategy or ReinstallStrategy))
        {
            return UpdaterResult.Fail(UpdaterExitCodes.ManualUpdateNeeded, $"this updater cannot apply updates with the '{record.Strategy}' strategy; update manually", installed);
        }

        if (CheckStaged(record, state) is { } notStaged)
        {
            return notStaged;
        }

        var staged = state.ReadStaged()!;
        try
        {
            UpdateClient.VerifyFile(staged.PathIn(state.Downloads), staged.ToArtifact(record.Target));
        }
        catch (UpdateException ex)
        {
            state.ClearDownloads();
            return UpdaterResult.Fail(UpdaterExitCodes.VerificationFailed, $"the downloaded update is damaged and was removed; download it again: {ex.Message}", installed);
        }

        if (options.WaitPid is { } pid && !environment.Processes.WaitForExit(pid, environment.WaitTimeout))
        {
            return UpdaterResult.Fail(UpdaterExitCodes.Cancelled, $"the application (process {pid}) did not exit within {environment.WaitTimeout.TotalSeconds:0} seconds", installed);
        }

        DeleteOldCopies();
        string copy;
        try
        {
            var directory = Path.Combine(environment.TempDirectory, CopyDirectoryPrefix + Guid.NewGuid().ToString("N"));

            // The copy keeps this executable's name (zexoro-updater.exe on Windows, zexoro-updater
            // elsewhere). On Unix the temporary directory is usually shared, so the copy lives in a
            // fresh directory only this user can enter, and nobody else can swap the program run next.
            copy = Path.Combine(directory, Path.GetFileName(environment.ExecutablePath));
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
                File.Copy(environment.ExecutablePath, copy);
            }
            else
            {
                Directory.CreateDirectory(directory, PrivateMode);
                File.SetUnixFileMode(directory, PrivateMode);
                File.Copy(environment.ExecutablePath, copy);
                File.SetUnixFileMode(copy, PrivateMode);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UpdaterResult.Fail(UpdaterExitCodes.ApplyFailed, $"cannot copy the updater to {environment.TempDirectory}: {ex.Message}", installed);
        }

        List<string> arguments = ["apply", "--from-root", root];
        if (options.Restart)
        {
            arguments.Add("--restart");
        }

        if (options.Json)
        {
            arguments.Add("--json");
        }

        var copyDirectory = Path.GetDirectoryName(copy)!;
        if (record.Strategy == ReinstallStrategy)
        {
            // The installer replaces <root>\zexoro-updater.exe, which cannot be written while this
            // process runs, so the copy waits for this process to exit and reports the outcome.
            arguments.AddRange(["--wait-pid", environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            environment.Processes.Start(copy, arguments, copyDirectory);
            return new UpdaterResult(UpdaterExitCodes.Success, UpdaterResult.Applied, "the update continues in the background") { Installed = installed, Relayed = true };
        }

        // replace: renaming a running executable is allowed, so this process can wait for the copy
        // and pass on its exit code. Its own file becomes zexoro-updater.exe.old, deleted next time.
        var exitCode = environment.Processes.RunAndWait(copy, arguments, copyDirectory);
        try
        {
            Directory.Delete(copyDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removed by the next apply.
        }

        return new UpdaterResult(exitCode, exitCode == 0 ? UpdaterResult.Applied : UpdaterResult.Error, $"the update finished with exit code {exitCode}") { Installed = installed, Relayed = true };
    }

    /// <summary>Runs as the copy in %TEMP%: applies the staged installer to <paramref name="root"/>.</summary>
    private UpdaterResult Apply(string root, UpdateInstallRecord record, UpdaterState state, ApplyOptions options)
    {
        var installed = record.Version.ToString();
        if (options.WaitPid is { } pid && !environment.Processes.WaitForExit(pid, environment.WaitTimeout))
        {
            return UpdaterResult.Fail(UpdaterExitCodes.Cancelled, $"process {pid} did not exit within {environment.WaitTimeout.TotalSeconds:0} seconds", installed);
        }

        if (CheckStaged(record, state) is { } notStaged)
        {
            return notStaged;
        }

        var staged = state.ReadStaged()!;
        var installer = staged.PathIn(state.Downloads);
        UpdaterResult result;

        // Held open with read-only sharing until the installer has exited, so the verified file
        // cannot be replaced between the check and its use.
        using (var locked = OpenLocked(installer, out var openError))
        {
            if (locked is null)
            {
                return UpdaterResult.Fail(UpdaterExitCodes.ApplyFailed, $"cannot open {installer}: {openError}", installed);
            }

            try
            {
                UpdateClient.VerifyStream(locked, staged.ToArtifact(record.Target));
            }
            catch (UpdateException ex)
            {
                locked.Dispose();
                state.ClearDownloads();
                return UpdaterResult.Fail(UpdaterExitCodes.VerificationFailed, $"the downloaded update is damaged and was removed; download it again: {ex.Message}", installed);
            }

            result = record.Strategy switch
            {
                ReplaceStrategy when record.Target.Format is ZipFormat or TarGzFormat => ReplaceFromArchive(root, record, staged, locked, options.Restart),
                ReplaceStrategy => Replace(root, record, staged, installer, options.Restart),
                ReinstallStrategy => Reinstall(root, record, staged, installer, options.Restart, movedUpdater: null),
                _ => UpdaterResult.Fail(UpdaterExitCodes.ManualUpdateNeeded, $"this updater cannot apply updates with the '{record.Strategy}' strategy; update manually", installed),
            };
        }

        // A verification failure here is an archive that cannot be a valid update: download it again.
        if (result.ExitCode is UpdaterExitCodes.Success or UpdaterExitCodes.UpToDate or UpdaterExitCodes.VerificationFailed)
        {
            state.ClearDownloads();
        }

        return result;
    }

    private UpdaterResult Replace(string root, UpdateInstallRecord record, StagedUpdate staged, string installer, bool restart)
    {
        var installed = record.Version.ToString();
        UninstallEntry? previous;
        UninstallEntry next;
        try
        {
            previous = environment.Registry.Read(record);
            next = new UninstallEntry(staged.Version.ToString(), NsisVersionOrder.Key(staged.Version), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or PlatformNotSupportedException or ArgumentException)
        {
            return UpdaterResult.Fail(UpdaterExitCodes.ApplyFailed, $"cannot read the uninstall entry: {ex.Message}", installed);
        }

        if (previous is null)
        {
            return UpdaterResult.Fail(UpdaterExitCodes.ApplyFailed, $"the uninstall entry of {record.App} is missing; reinstall the application", installed);
        }

        try
        {
            FileSwap.DeleteLeftovers(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UpdaterResult.Fail(UpdaterExitCodes.ApplyFailed, $"cannot remove what an earlier update left in {root}: {ex.Message}", installed);
        }

        log.WriteLine($"Staging {record.App} {staged.Version} in {root}");
        // The installer refuses to stage unless this root is the existing install's location.
        var exitCode = environment.Processes.RunAndWait(
            installer,
            ["/S", NsisScript.StageOption],
            environment.TempDirectory,
            new Dictionary<string, string>(StringComparer.Ordinal) { [NsisScript.StageRootVariable] = root });
        if (exitCode != 0)
        {
            return Discard(root, $"the installer exited with code {exitCode} in stage mode; nothing was changed", installed);
        }

        UpdateInstallRecord stagedRecord;
        try
        {
            stagedRecord = UpdateInstallRecord.Load(Path.Combine(root, UpdateInstallRecord.FileName + FileSwap.NewSuffix));
        }
        catch (UpdateException ex)
        {
            return Discard(root, $"the installer staged no valid install record: {ex.Message}", installed);
        }

        if (stagedRecord.App != record.App || stagedRecord.Target != record.Target || stagedRecord.Strategy != record.Strategy
            || stagedRecord.Version.CompareTo(staged.Version) != 0)
        {
            return Discard(
                root,
                $"the installer staged {stagedRecord.App} {stagedRecord.Version} ({stagedRecord.Target}, {stagedRecord.Strategy}), not {record.App} {staged.Version} ({record.Target}, {record.Strategy})",
                installed);
        }

        if (!string.Equals(stagedRecord.Main, record.Main, StringComparison.Ordinal))
        {
            // Shortcuts point at app\<main>; only the installer can move them.
            log.WriteLine($"The main executable changed ({record.Main} -> {stagedRecord.Main}); running the installer to upgrade in place");
            DeleteLeftoversQuietly(root);
            return Reinstall(root, record, staged, installer, restart, movedUpdater: MoveUpdaterAside(root));
        }

        FileSwap swap;
        try
        {
            swap = FileSwap.Plan(root);
        }
        catch (InvalidOperationException ex)
        {
            return Discard(root, ex.Message, installed);
        }

        next = next with { EstimatedSizeKb = SizeKb(Path.Combine(root, FileSwap.AppDirectoryName + FileSwap.NewSuffix)) };
        try
        {
            swap.Apply(environment.Mover);
        }
        catch (SwapException ex) when (ex.RolledBack)
        {
            return Discard(root, $"the update failed and was rolled back: {ex.Message}", installed);
        }
        catch (SwapException ex)
        {
            return UpdaterResult.Fail(UpdaterExitCodes.RollbackFailed, $"the update failed and could not be rolled back; reinstall the application: {ex.Message}", installed);
        }

        try
        {
            environment.Registry.Write(record, next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or PlatformNotSupportedException)
        {
            var message = $"cannot update the uninstall entry: {ex.Message}";
            try
            {
                environment.Registry.Write(record, previous);
            }
            catch (Exception restore) when (restore is IOException or UnauthorizedAccessException or System.Security.SecurityException or PlatformNotSupportedException)
            {
                log.WriteLine($"warning: cannot restore the uninstall entry: {restore.Message}");
            }

            try
            {
                swap.Rollback(environment.Mover);
            }
            catch (SwapException rollback)
            {
                return UpdaterResult.Fail(UpdaterExitCodes.RollbackFailed, $"{message}; {rollback.Message}; reinstall the application", installed);
            }

            return Discard(root, $"{message}; the update was rolled back", installed);
        }

        foreach (var left in FileSwap.DeleteOld(root))
        {
            log.WriteLine($"{left} is in use; it is removed by the next update");
        }

        if (restart)
        {
            StartApplication(root, stagedRecord);
        }

        return Applied(record, staged);
    }

    /// <summary>
    /// <c>replace</c> for a portable zip or tar.gz: extracts the verified archive into <c>app.new</c> and the
    /// <c>.new</c> root files, checks the staged record, then swaps by rename. Nothing is registered,
    /// so nothing else is written; a changed <c>main</c> is fine, as there are no shortcuts.
    /// </summary>
    private UpdaterResult ReplaceFromArchive(string root, UpdateInstallRecord record, StagedUpdate staged, Stream archive, bool restart)
    {
        var installed = record.Version.ToString();
        if (CheckWritable(root) is { } notWritable)
        {
            return UpdaterResult.Fail(UpdaterExitCodes.ManualUpdateNeeded, notWritable, installed);
        }

        try
        {
            FileSwap.DeleteLeftovers(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UpdaterResult.Fail(UpdaterExitCodes.ApplyFailed, $"cannot remove what an earlier update left in {root}: {ex.Message}", installed);
        }

        log.WriteLine($"Extracting {record.App} {staged.Version} into {root}");
        try
        {
            ArchiveStaging.Extract(archive, root, record.Target.Format);
        }
        catch (InvalidArchiveException ex)
        {
            DeleteLeftoversQuietly(root);
            return UpdaterResult.Fail(UpdaterExitCodes.VerificationFailed, $"{ex.Message}; nothing was changed and the download was removed", installed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Discard(root, $"cannot extract the update: {ex.Message}; nothing was changed and the download was removed", installed);
        }

        UpdateInstallRecord stagedRecord;
        try
        {
            stagedRecord = UpdateInstallRecord.Load(Path.Combine(root, UpdateInstallRecord.FileName + FileSwap.NewSuffix));
        }
        catch (UpdateException ex)
        {
            return Discard(root, $"the update archive holds no valid install record: {ex.Message}", installed);
        }

        if (stagedRecord.App != record.App || stagedRecord.Target != record.Target || stagedRecord.Strategy != record.Strategy
            || stagedRecord.Version.CompareTo(staged.Version) != 0)
        {
            return Discard(
                root,
                $"the update archive holds {stagedRecord.App} {stagedRecord.Version} ({stagedRecord.Target}, {stagedRecord.Strategy}), not {record.App} {staged.Version} ({record.Target}, {record.Strategy})",
                installed);
        }

        FileSwap swap;
        try
        {
            swap = FileSwap.Plan(root);
        }
        catch (InvalidOperationException ex)
        {
            return Discard(root, ex.Message, installed);
        }

        try
        {
            swap.Apply(environment.Mover);
        }
        catch (SwapException ex) when (ex.RolledBack)
        {
            return Discard(root, $"the update failed and was rolled back: {ex.Message}", installed);
        }
        catch (SwapException ex)
        {
            return UpdaterResult.Fail(UpdaterExitCodes.RollbackFailed, $"the update failed and could not be rolled back; extract the new version again: {ex.Message}", installed);
        }

        foreach (var left in FileSwap.DeleteOld(root))
        {
            log.WriteLine($"{left} is in use; it is removed by the next update");
        }

        if (restart)
        {
            StartApplication(root, stagedRecord);
        }

        return Applied(record, staged);
    }

    /// <summary>
    /// Returns why the updater cannot write into <paramref name="root"/> (a folder the user may not
    /// change), or null when it can. Probed by creating and deleting a file.
    /// </summary>
    private static string? CheckWritable(string root)
    {
        var probe = Path.Combine(root, $".zexoro-write-check-{Guid.NewGuid():N}");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"the folder {root} is not writable, so the update cannot be applied; move the application to a folder you can write to, or extract the new version yourself ({ex.Message})";
        }
        catch (IOException)
        {
            // Not a permission problem; extraction reports it if it persists.
            return null;
        }
    }

    /// <param name="movedUpdater">
    /// The root updater, renamed aside so a per-user installer can write a new one while the
    /// handing-off updater still runs; restored when the installer writes none.
    /// </param>
    private UpdaterResult Reinstall(string root, UpdateInstallRecord record, StagedUpdate staged, string installer, bool restart, string? movedUpdater)
    {
        var installed = record.Version.ToString();
        log.WriteLine($"Running the installer of {record.App} {staged.Version}");
        int? exitCode;
        try
        {
            exitCode = environment.Processes.ShellExecuteAndWait(installer, "/S", environment.TempDirectory);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            RestoreUpdater(root, movedUpdater);
            return UpdaterResult.Fail(UpdaterExitCodes.ApplyFailed, $"cannot start the installer: {ex.Message}", installed);
        }

        if (exitCode != 0)
        {
            RestoreUpdater(root, movedUpdater);
        }

        switch (exitCode)
        {
            case null:
                return UpdaterResult.Fail(UpdaterExitCodes.Cancelled, "the installer was not allowed to run (elevation was declined)", installed);
            case InstallerNewerVersionInstalled:
                return new UpdaterResult(UpdaterExitCodes.UpToDate, UpdaterResult.UpToDate, "a newer version is already installed") { Installed = installed };
            case not 0:
                return UpdaterResult.Fail(UpdaterExitCodes.ApplyFailed, $"the installer exited with code {exitCode}", installed);
        }

        var current = record;
        try
        {
            current = UpdateInstallRecord.Load(Path.Combine(root, UpdateInstallRecord.FileName));
        }
        catch (UpdateException ex)
        {
            log.WriteLine($"warning: {ex.Message}");
        }

        if (restart)
        {
            StartApplication(root, current);
        }

        return Applied(record, staged);
    }

    private static UpdaterResult Applied(UpdateInstallRecord record, StagedUpdate staged) =>
        new(UpdaterExitCodes.Success, UpdaterResult.Applied, $"{record.App} {staged.Version} is installed")
        {
            Installed = record.Version.ToString(),
            Version = staged.Version.ToString(),
            Notes = staged.Notes,
            Size = staged.Size,
        };

    /// <summary>Returns a result when there is nothing to apply; null when a newer update is staged.</summary>
    private static UpdaterResult? CheckStaged(UpdateInstallRecord record, UpdaterState state)
    {
        var installed = record.Version.ToString();
        var staged = state.ReadStaged();
        if (staged is null)
        {
            return new UpdaterResult(UpdaterExitCodes.UpToDate, UpdaterResult.UpToDate, "no update is downloaded; run download first") { Installed = installed };
        }

        if (staged.App != record.App || staged.Version <= record.Version)
        {
            state.ClearDownloads();
            return new UpdaterResult(UpdaterExitCodes.UpToDate, UpdaterResult.UpToDate, $"{record.App} {installed} is up to date") { Installed = installed };
        }

        return null;
    }

    private UpdaterResult Discard(string root, string message, string installed)
    {
        DeleteLeftoversQuietly(root);
        return UpdaterResult.Fail(UpdaterExitCodes.ApplyFailed, message, installed);
    }

    private void DeleteLeftoversQuietly(string root)
    {
        try
        {
            FileSwap.DeleteLeftovers(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"warning: {ex.Message}; it is removed by the next update");
        }
    }

    private string? MoveUpdaterAside(string root)
    {
        var path = Path.Combine(root, UpdaterFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            environment.Mover.Move(path, path + FileSwap.OldSuffix);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"warning: cannot move {path} aside: {ex.Message}");
            return null;
        }
    }

    private void RestoreUpdater(string root, string? moved)
    {
        if (moved is null || File.Exists(moved))
        {
            return;
        }

        try
        {
            environment.Mover.Move(moved + FileSwap.OldSuffix, moved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"warning: cannot restore {moved} in {root}: {ex.Message}");
        }
    }

    private void StartApplication(string root, UpdateInstallRecord record)
    {
        if (record.Main is null)
        {
            log.WriteLine("warning: the install record names no main executable; not restarting");
            return;
        }

        var path = Path.Combine(root, FileSwap.AppDirectoryName, record.Main.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            environment.Processes.Start(path, [], Path.GetDirectoryName(path)!);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            log.WriteLine($"warning: cannot start {path}: {ex.Message}");
        }
    }

    private static FileStream? OpenLocked(string path, out string? error)
    {
        try
        {
            error = null;
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Removes copies left in %TEMP% by earlier runs. On Windows those still running stay, as their
    /// files cannot be deleted. Unix deletes running programs without complaint, so there only copies
    /// older than <see cref="UnixCopyMaxAge"/> are removed: a concurrent apply of another application
    /// may have just created its copy and not started it yet.
    /// </summary>
    private void DeleteOldCopies()
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(environment.TempDirectory, CopyDirectoryPrefix + "*"))
            {
                try
                {
                    if (!OperatingSystem.IsWindows() && DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) < UnixCopyMaxAge)
                    {
                        continue;
                    }

                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use by another run.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to clean.
        }
    }

    /// <summary>The size of a directory tree in KiB, rounded up, as the installer computes it.</summary>
    internal static uint SizeKb(string directory)
    {
        long bytes = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            bytes += file.Length;
        }

        return (uint)Math.Min((bytes + 1023) / 1024, uint.MaxValue);
    }
}
