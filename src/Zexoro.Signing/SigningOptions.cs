namespace Zexoro.Signing;

/// <summary>How <see cref="AuthenticodeSigner"/> signs a file.</summary>
public sealed class SigningOptions
{
    /// <summary>The certificate, private key and chain to sign with.</summary>
    public required SigningCredential Credential { get; init; }

    /// <summary>RFC 3161 timestamp authority; <see langword="null"/> signs without a timestamp.</summary>
    public Uri? TimestampUrl { get; init; }

    /// <summary>
    /// The client used to reach <see cref="TimestampUrl"/>. When <see langword="null"/>, a client
    /// with <see cref="TimestampTimeout"/> is created for each call.
    /// </summary>
    public HttpClient? HttpClient { get; init; }

    /// <summary>Timeout for the timestamp request when no <see cref="HttpClient"/> is supplied.</summary>
    public TimeSpan TimestampTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Program name shown by Windows in the signature details (SpcSpOpusInfo), optional.</summary>
    public string? Description { get; init; }

    /// <summary>"More information" URL shown by Windows in the signature details (SpcSpOpusInfo), optional.</summary>
    public Uri? DescriptionUrl { get; init; }
}
