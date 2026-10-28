using Zexoro.Updates;
using Zexoro.Versioning;

namespace Zexoro.Updater;

/// <summary><c>check</c> and <c>download</c>.</summary>
internal sealed class UpdateCommands(UpdaterEnvironment environment, TextWriter log)
{
    public async Task<UpdaterResult> CheckAsync(bool download, CancellationToken cancellationToken)
    {
        UpdateInstallRecord? record = null;
        try
        {
            record = UpdateInstallRecord.Load(InstallLayout.RecordPath(environment.DefaultRoot));
            if (download && record.Strategy == Notify.Strategy)
            {
                return Notify.ManualUpdate(record, "download");
            }

            var installed = record.ToInstalledApp();
            var state = new UpdaterState(environment.StateBase, record.App);
            using var http = environment.CreateHttpClient();
            var client = new UpdateClient(http);
            var selection = await client.CheckAsync(installed, new FeedSequenceStore(state.SequenceFile), UpdaterVersion.Current, cancellationToken).ConfigureAwait(false);
            var current = record.Version.ToString();
            switch (selection.Status)
            {
                case UpdateStatus.UpToDate:
                    return new UpdaterResult(UpdaterExitCodes.UpToDate, UpdaterResult.UpToDate, $"{record.App} {current} is up to date") { Installed = current };
                case UpdateStatus.UpdaterTooOld:
                    return new UpdaterResult(
                        UpdaterExitCodes.ManualUpdateNeeded,
                        UpdaterResult.Error,
                        $"{selection.Release!.Version} needs updater {selection.Release.MinimumUpdater} or newer (this is {UpdaterVersion.Current}); update manually")
                    {
                        Installed = current,
                        Version = selection.Release.Version.ToString(),
                        Notes = selection.Release.Notes,
                        Size = selection.Artifact?.Size,
                        Url = selection.Artifact?.Url.AbsoluteUri,
                    };
            }

            var release = selection.Release!;
            var artifact = selection.Artifact!;
            var result = new UpdaterResult(UpdaterExitCodes.Success, UpdaterResult.Available, $"{record.App} {release.Version} is available (installed: {current})")
            {
                Installed = current,
                Version = release.Version.ToString(),
                Notes = release.Notes,
                Size = artifact.Size,
                Url = artifact.Url.AbsoluteUri,
            };
            if (!download)
            {
                return result;
            }

            await StageAsync(client, state, installed, release, artifact, cancellationToken).ConfigureAwait(false);
            return result with { Status = UpdaterResult.Downloaded, Message = $"{record.App} {release.Version} is downloaded and verified; run apply to install it" };
        }
        catch (UpdateException ex)
        {
            return UpdaterResult.Fail(UpdaterExitCodes.For(ex.Kind), ex.Message, record?.Version.ToString());
        }
        catch (InvalidDataException ex)
        {
            return UpdaterResult.Fail(UpdaterExitCodes.VerificationFailed, ex.Message, record?.Version.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UpdaterResult.Fail(UpdaterExitCodes.Network, ex.Message, record?.Version.ToString());
        }
    }

    private async Task StageAsync(UpdateClient client, UpdaterState state, InstalledApp installed, FeedRelease release, FeedArtifact artifact, CancellationToken cancellationToken)
    {
        if (state.ReadStaged() is { } staged && staged.Version == release.Version && staged.Sha256 == artifact.Sha256)
        {
            try
            {
                UpdateClient.VerifyFile(staged.PathIn(state.Downloads), artifact);
                log.WriteLine($"{release.Version} is already downloaded");
                return;
            }
            catch (UpdateException)
            {
                // Damaged since it was staged: download it again.
            }
        }

        state.ClearDownloads();
        var file = FileNameFor(installed.App, release.Version, artifact.Url, ExtensionFor(installed.Target.Format));
        log.WriteLine($"Downloading {artifact.Url} ({artifact.Size} bytes)");
        await client.DownloadAsync(artifact, Path.Combine(state.Downloads, file), cancellationToken).ConfigureAwait(false);
        state.WriteStaged(new StagedUpdate(installed.App, installed.Channel, release.Version, release.Notes, file, artifact.Size, artifact.Sha256, artifact.Url));
    }

    /// <summary>The downloaded file's extension: <c>.zip</c> for a portable zip or a macOS bundle, <c>.tar.gz</c> for a tar.gz, <c>.exe</c> (an installer) otherwise.</summary>
    internal static string ExtensionFor(string format) => format switch
    {
        ApplyCommand.ZipFormat or ApplyCommand.AppFormat => ".zip",
        ApplyCommand.TarGzFormat => ".tar.gz",
        _ => ".exe",
    };

    /// <summary>
    /// The URL's file name when it is a plain name with <paramref name="extension"/>, otherwise one
    /// made from the app and version.
    /// </summary>
    internal static string FileNameFor(string app, SemanticVersion version, Uri url, string extension)
    {
        var name = Path.GetFileName(Uri.UnescapeDataString(url.AbsolutePath));
        var plain = name.Length > extension.Length && name.Length <= 128
            && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
            && !name.StartsWith('.');
        return plain ? name : $"{app}-{version}{extension}";
    }
}
