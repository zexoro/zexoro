namespace Zexoro.Updates;

/// <summary>Why an update step failed; the updater maps each kind to an exit code (docs/design/0005-nsis-updates.md).</summary>
public enum UpdateErrorKind
{
    /// <summary>The feed or an artifact could not be fetched (network or HTTP failure).</summary>
    Network,

    /// <summary>The feed signature does not verify against any trusted key.</summary>
    Signature,

    /// <summary>The signed feed is malformed, or is for another application or channel.</summary>
    InvalidFeed,

    /// <summary>The feed's sequence is lower than one already accepted (a replayed old feed).</summary>
    Replay,

    /// <summary>A downloaded artifact does not match the size or SHA-256 in the feed.</summary>
    Integrity,

    /// <summary>A URL is not HTTPS (plain HTTP is accepted only for localhost).</summary>
    InsecureUrl,

    /// <summary>The feed needs a newer updater: its schema is too new, or a release requires a newer updater.</summary>
    UpdaterTooOld,

    /// <summary>A key or key file is not valid.</summary>
    InvalidKey,
}

/// <summary>An update step failed. The message is fit for the user.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(UpdateErrorKind kind, string message)
        : base(message) => Kind = kind;

    public UpdateException(UpdateErrorKind kind, string message, Exception innerException)
        : base(message, innerException) => Kind = kind;

    public UpdateErrorKind Kind { get; }
}
