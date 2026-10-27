using Zexoro.Versioning;

namespace Zexoro.Updates;

/// <summary>The outcome of comparing a feed with an install.</summary>
public enum UpdateStatus
{
    /// <summary>No release for this target is newer than the installed version.</summary>
    UpToDate,

    /// <summary><see cref="UpdateSelection.Release"/> can be installed by this updater.</summary>
    Available,

    /// <summary>Newer releases exist, but every one of them requires a newer updater; the user must update manually.</summary>
    UpdaterTooOld,
}

/// <summary>What <see cref="ReleaseSelector.Select"/> found.</summary>
/// <param name="Status">The outcome.</param>
/// <param name="Release">The release to install (<see cref="UpdateStatus.Available"/>), or the newest one that needs a newer updater (<see cref="UpdateStatus.UpdaterTooOld"/>); null when up to date.</param>
/// <param name="Artifact">The artifact of <paramref name="Release"/> for the install's target.</param>
/// <param name="Newest">The newest release newer than the installed version for the target, even if this updater cannot apply it.</param>
public sealed record UpdateSelection(UpdateStatus Status, FeedRelease? Release, FeedArtifact? Artifact, FeedRelease? Newest);

/// <summary>Chooses the release to install (docs/design/0002-update-contract.md section 3).</summary>
public static class ReleaseSelector
{
    /// <summary>
    /// Returns the release with the highest precedence that is strictly newer than
    /// <paramref name="installed"/>, has an artifact for <paramref name="target"/>, and does not
    /// require an updater newer than <paramref name="updaterVersion"/>. Build metadata is ignored;
    /// pre-releases are ordinary versions (channels, not pre-release tags, separate audiences).
    /// </summary>
    public static UpdateSelection Select(UpdateFeed feed, SemanticVersion installed, UpdateTarget target, SemanticVersion updaterVersion)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(updaterVersion);

        // Releases are kept highest first.
        var candidates = feed.Releases
            .Where(r => r.Version > installed && r.FindArtifact(target) is not null)
            .ToList();
        if (candidates.Count == 0)
        {
            return new UpdateSelection(UpdateStatus.UpToDate, null, null, null);
        }

        var newest = candidates[0];
        var applicable = candidates.FirstOrDefault(r => r.MinimumUpdater is null || r.MinimumUpdater <= updaterVersion);
        return applicable is null
            ? new UpdateSelection(UpdateStatus.UpdaterTooOld, newest, newest.FindArtifact(target), newest)
            : new UpdateSelection(UpdateStatus.Available, applicable, applicable.FindArtifact(target), newest);
    }
}
