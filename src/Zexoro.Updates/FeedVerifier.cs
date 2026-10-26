namespace Zexoro.Updates;

/// <summary>Turns downloaded feed bytes into a trusted <see cref="UpdateFeed"/>.</summary>
public static class FeedVerifier
{
    /// <summary>
    /// Verifies the detached signature over the exact bytes of <paramref name="feed"/> and only then
    /// parses them (docs/design/0002-update-contract.md section 5). Unverified bytes are never interpreted.
    /// </summary>
    /// <param name="feed">The bytes of <c>feed.json</c> as downloaded.</param>
    /// <param name="signatureFile">The bytes of <c>feed.json.sig</c>.</param>
    /// <param name="trustedKeys">The install's trusted keys; any one of them may have signed the feed.</param>
    /// <param name="expectedApp">When not null, the feed must be for this application id.</param>
    /// <param name="expectedChannel">When not null, the feed must be for this channel.</param>
    /// <exception cref="UpdateException">
    /// <see cref="UpdateErrorKind.Signature"/> when no trusted key verifies the signature; otherwise
    /// as <see cref="UpdateFeed.Parse"/>, and <see cref="UpdateErrorKind.InvalidFeed"/> for another
    /// application or channel.
    /// </exception>
    public static UpdateFeed Verify(
        ReadOnlySpan<byte> feed,
        ReadOnlySpan<byte> signatureFile,
        IReadOnlyCollection<UpdatePublicKey> trustedKeys,
        string? expectedApp = null,
        string? expectedChannel = null)
    {
        ArgumentNullException.ThrowIfNull(trustedKeys);
        if (trustedKeys.Count == 0)
        {
            throw new UpdateException(UpdateErrorKind.Signature, "no trusted update keys are configured");
        }

        if (!FeedSignature.Verify(feed, signatureFile, trustedKeys))
        {
            throw new UpdateException(UpdateErrorKind.Signature, "the feed signature does not match any trusted key");
        }

        var parsed = UpdateFeed.Parse(feed);
        if (expectedApp is not null && !string.Equals(parsed.App, expectedApp, StringComparison.Ordinal))
        {
            throw new UpdateException(UpdateErrorKind.InvalidFeed, $"the feed is for {parsed.App}, not {expectedApp}");
        }

        if (expectedChannel is not null && !string.Equals(parsed.Channel, expectedChannel, StringComparison.Ordinal))
        {
            throw new UpdateException(UpdateErrorKind.InvalidFeed, $"the feed is for channel '{parsed.Channel}', not '{expectedChannel}'");
        }

        return parsed;
    }
}
