namespace Zexoro.Updates;

/// <summary>
/// A release fragment (<c>&lt;installer&gt;.release.json</c>): what one <c>zexoro pack</c> run
/// contributes to a channel's feed, merged in and signed by <c>zexoro feed add</c>
/// (docs/design/0005-nsis-updates.md section 2).
/// </summary>
public sealed class ReleaseFragment
{
    public const int SupportedSchema = 1;

    /// <exception cref="UpdateException">The application id or channel is not valid (<see cref="UpdateErrorKind.InvalidFeed"/>).</exception>
    public ReleaseFragment(string app, string channel, FeedRelease release)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(release);
        FeedRules.RequireAppId(app);
        FeedRules.RequireChannel(channel);
        App = app;
        Channel = channel;
        Release = release;
    }

    public string App { get; }

    public string Channel { get; }

    public FeedRelease Release { get; }

    /// <exception cref="UpdateException">The fragment is malformed or has a newer schema.</exception>
    public static ReleaseFragment Parse(ReadOnlySpan<byte> json) => FeedJson.ParseFragment(json);

    /// <summary>Returns the fragment as indented UTF-8 JSON with LF line ends and a final newline.</summary>
    public byte[] ToJson() => FeedJson.Write(this);

    /// <summary>
    /// Merges this fragment into <paramref name="feed"/> (see <see cref="UpdateFeed.Add"/>), or starts a
    /// new feed with sequence 1 when <paramref name="feed"/> is null.
    /// </summary>
    /// <exception cref="UpdateException">The feed is for another application or channel, or the release conflicts with it.</exception>
    public UpdateFeed AddTo(UpdateFeed? feed, DateTimeOffset published)
    {
        if (feed is null)
        {
            return new UpdateFeed(App, Channel, 1, published, [Release]);
        }

        if (!string.Equals(feed.App, App, StringComparison.Ordinal) || !string.Equals(feed.Channel, Channel, StringComparison.Ordinal))
        {
            throw FeedRules.Invalid(
                $"the fragment is for {App} on channel '{Channel}', but the feed is for {feed.App} on channel '{feed.Channel}'");
        }

        return feed.Add(Release, published);
    }
}
