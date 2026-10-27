using Zexoro.Versioning;

namespace Zexoro.Updates;

/// <summary>What an install is: the part of the install record that selects artifacts.</summary>
/// <param name="Os">Operating system, e.g. <c>windows</c>.</param>
/// <param name="Arch">Architecture, e.g. <c>x64</c>.</param>
/// <param name="Format">Package format, e.g. <c>nsis</c>.</param>
/// <param name="Scope">Install scope, e.g. <c>user</c> or <c>machine</c>.</param>
public sealed record UpdateTarget(string Os, string Arch, string Format, string Scope)
{
    public override string ToString() => $"{Os}/{Arch}/{Format}/{Scope}";
}

/// <summary>One downloadable package of a release.</summary>
public sealed class FeedArtifact
{
    /// <exception cref="UpdateException">A value is not valid (<see cref="UpdateErrorKind.InvalidFeed"/> or <see cref="UpdateErrorKind.InsecureUrl"/>).</exception>
    public FeedArtifact(UpdateTarget target, Uri url, long size, string sha256)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(sha256);
        FeedRules.RequireToken(target.Os, "os");
        FeedRules.RequireToken(target.Arch, "arch");
        FeedRules.RequireToken(target.Format, "format");
        FeedRules.RequireToken(target.Scope, "scope");
        UpdateUrl.EnsureAllowed(url);
        if (size <= 0)
        {
            throw FeedRules.Invalid("size must be a positive number of bytes");
        }

        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
        {
            throw FeedRules.Invalid("sha256 must be 64 hexadecimal digits");
        }

        Target = target;
        Url = url;
        Size = size;
        Sha256 = sha256.ToLowerInvariant();
    }

    public UpdateTarget Target { get; }

    /// <summary>Where to download it: https, or http on localhost.</summary>
    public Uri Url { get; }

    /// <summary>The exact size in bytes.</summary>
    public long Size { get; }

    /// <summary>The SHA-256 of the file, in lower-case hexadecimal.</summary>
    public string Sha256 { get; }

    /// <summary>Whether both describe the same file for the same target.</summary>
    internal bool SameAs(FeedArtifact other) =>
        Target == other.Target && Url == other.Url && Size == other.Size && Sha256 == other.Sha256;
}

/// <summary>One version of the application in a feed.</summary>
public sealed class FeedRelease
{
    /// <exception cref="UpdateException">Two artifacts are for the same target (<see cref="UpdateErrorKind.InvalidFeed"/>).</exception>
    public FeedRelease(SemanticVersion version, IEnumerable<FeedArtifact> artifacts, string? notes = null, SemanticVersion? minimumUpdater = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(artifacts);
        var list = artifacts.ToList();
        var duplicate = list.GroupBy(a => a.Target).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw FeedRules.Invalid($"release {version} lists more than one artifact for {duplicate.Key}");
        }

        Version = version;
        Artifacts = list;
        Notes = string.IsNullOrEmpty(notes) ? null : notes;
        MinimumUpdater = minimumUpdater;
    }

    public SemanticVersion Version { get; }

    /// <summary>Release notes, usually a URL; only reported, never fetched by the updater.</summary>
    public string? Notes { get; }

    /// <summary>The oldest updater that may apply this release, or null for any.</summary>
    public SemanticVersion? MinimumUpdater { get; }

    public IReadOnlyList<FeedArtifact> Artifacts { get; }

    /// <summary>Returns the artifact for <paramref name="target"/>, or null.</summary>
    public FeedArtifact? FindArtifact(UpdateTarget target) => Artifacts.FirstOrDefault(a => a.Target == target);
}

/// <summary>
/// A parsed <c>feed.json</c> (docs/design/0002-update-contract.md section 4). Use
/// <see cref="FeedVerifier"/> for downloaded feeds: it verifies the signature before parsing.
/// </summary>
public sealed class UpdateFeed
{
    /// <summary>The highest <c>schema</c> this version understands.</summary>
    public const int SupportedSchema = 1;

    /// <exception cref="UpdateException">A value is not valid, or two releases have the same precedence (<see cref="UpdateErrorKind.InvalidFeed"/>).</exception>
    public UpdateFeed(string app, string channel, long sequence, DateTimeOffset published, IEnumerable<FeedRelease> releases)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(releases);
        FeedRules.RequireAppId(app);
        FeedRules.RequireChannel(channel);
        if (sequence < 1)
        {
            throw FeedRules.Invalid("sequence must be at least 1");
        }

        var list = releases.OrderByDescending(r => r.Version).ToList();
        for (var i = 1; i < list.Count; i++)
        {
            if (list[i].Version.CompareTo(list[i - 1].Version) == 0)
            {
                throw FeedRules.Invalid($"versions {list[i - 1].Version} and {list[i].Version} have the same precedence");
            }
        }

        App = app;
        Channel = channel;
        Sequence = sequence;
        Published = published.ToUniversalTime();
        Releases = list;
    }

    /// <summary>The application id (<c>app.id</c>).</summary>
    public string App { get; }

    public string Channel { get; }

    /// <summary>Increases with every publish; the updater rejects a lower one than it has seen.</summary>
    public long Sequence { get; }

    public DateTimeOffset Published { get; }

    /// <summary>The releases, highest precedence first.</summary>
    public IReadOnlyList<FeedRelease> Releases { get; }

    /// <summary>
    /// Parses feed bytes without checking a signature: only for feeds the caller produced itself
    /// (<c>zexoro feed add</c>). Downloaded feeds go through <see cref="FeedVerifier.Verify"/>.
    /// </summary>
    /// <exception cref="UpdateException">
    /// The feed is malformed (<see cref="UpdateErrorKind.InvalidFeed"/>), uses an insecure URL, or has a
    /// newer schema (<see cref="UpdateErrorKind.UpdaterTooOld"/>).
    /// </exception>
    public static UpdateFeed Parse(ReadOnlySpan<byte> json) => FeedJson.ParseFeed(json);

    /// <summary>Returns the feed as indented UTF-8 JSON with LF line ends and a final newline.</summary>
    public byte[] ToJson() => FeedJson.Write(this);

    /// <summary>
    /// Returns the feed with <paramref name="release"/> merged in, or this feed when it is already
    /// there. A release with the same version gains the new artifacts; the same artifact again is a
    /// no-op, a different file for the same target is an error. The new feed has the next sequence
    /// number and <paramref name="published"/>.
    /// </summary>
    /// <exception cref="UpdateException">The release conflicts with the feed (<see cref="UpdateErrorKind.InvalidFeed"/>).</exception>
    public UpdateFeed Add(FeedRelease release, DateTimeOffset published)
    {
        ArgumentNullException.ThrowIfNull(release);
        var existing = Releases.FirstOrDefault(r => r.Version.CompareTo(release.Version) == 0);
        if (existing is null)
        {
            return new UpdateFeed(App, Channel, Sequence + 1, published, [.. Releases, release]);
        }

        if (existing.Version != release.Version)
        {
            throw FeedRules.Invalid($"the feed already has version {existing.Version}, which has the same precedence as {release.Version}");
        }

        var artifacts = existing.Artifacts.ToList();
        var changed = false;
        foreach (var artifact in release.Artifacts)
        {
            var current = artifacts.FirstOrDefault(a => a.Target == artifact.Target);
            if (current is null)
            {
                artifacts.Add(artifact);
                changed = true;
            }
            else if (!current.SameAs(artifact))
            {
                throw FeedRules.Invalid(
                    $"version {release.Version} already has a different artifact for {artifact.Target}; publish a new version instead of replacing a published file");
            }
        }

        if (!changed)
        {
            return this;
        }

        var merged = new FeedRelease(existing.Version, artifacts, existing.Notes ?? release.Notes, Max(existing.MinimumUpdater, release.MinimumUpdater));
        return new UpdateFeed(App, Channel, Sequence + 1, published, Releases.Select(r => r == existing ? merged : r));
    }

    private static SemanticVersion? Max(SemanticVersion? left, SemanticVersion? right) =>
        left is null ? right : right is null ? left : (left >= right ? left : right);
}

/// <summary>Value rules shared by the feed and fragment models.</summary>
internal static class FeedRules
{
    public static UpdateException Invalid(string message) => new(UpdateErrorKind.InvalidFeed, message);

    public static void RequireAppId(string app)
    {
        if (app.Length == 0 || app.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-'))
        {
            throw Invalid($"'{app}' is not a valid application id");
        }
    }

    /// <summary>A channel names a feed and appears in URLs: ASCII letters, digits, '.', '-' and '_'.</summary>
    public static bool IsValidChannel(string channel) =>
        channel.Length > 0 && channel.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') && channel[0] != '.';

    public static void RequireChannel(string channel)
    {
        if (!IsValidChannel(channel))
        {
            throw Invalid($"'{channel}' is not a valid channel (ASCII letters, digits, '.', '-' and '_')");
        }
    }

    public static void RequireToken(string value, string name)
    {
        if (string.IsNullOrEmpty(value) || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
        {
            throw Invalid($"{name} '{value}' is not valid");
        }
    }
}
