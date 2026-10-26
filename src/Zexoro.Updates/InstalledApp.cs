using Zexoro.Versioning;

namespace Zexoro.Updates;

/// <summary>What the updater knows about the install it updates, taken from its install record.</summary>
/// <param name="App">The application id.</param>
/// <param name="Channel">The channel the install follows.</param>
/// <param name="Version">The installed version.</param>
/// <param name="Target">The install's os, arch, format and scope.</param>
/// <param name="Feed">The channel's feed URL.</param>
/// <param name="Keys">The trusted feed keys.</param>
public sealed record InstalledApp(
    string App,
    string Channel,
    SemanticVersion Version,
    UpdateTarget Target,
    Uri Feed,
    IReadOnlyList<UpdatePublicKey> Keys);

/// <summary>The steps of an update check that need no network, in contract order.</summary>
public static class UpdateCheck
{
    /// <summary>
    /// Verifies the signature (before parsing), the application and channel, rejects a replayed
    /// feed, records the accepted sequence in <paramref name="sequences"/>, and selects a release.
    /// </summary>
    /// <exception cref="UpdateException">Verification failed or the feed's schema is too new.</exception>
    /// <exception cref="InvalidDataException">The sequence state file is damaged.</exception>
    /// <exception cref="IOException">The sequence state file cannot be written.</exception>
    public static UpdateSelection Evaluate(
        InstalledApp installed,
        ReadOnlySpan<byte> feed,
        ReadOnlySpan<byte> signatureFile,
        FeedSequenceStore sequences,
        SemanticVersion updaterVersion)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(sequences);
        var verified = FeedVerifier.Verify(feed, signatureFile, installed.Keys, installed.App, installed.Channel);
        sequences.Accept(verified);
        return ReleaseSelector.Select(verified, installed.Version, installed.Target, updaterVersion);
    }
}
