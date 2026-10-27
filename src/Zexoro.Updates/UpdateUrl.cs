namespace Zexoro.Updates;

/// <summary>
/// The transport rule of the update contract (docs/design/0002-update-contract.md section 5): feeds
/// and artifacts are fetched over HTTPS; plain HTTP is accepted only for loopback hosts, for tests.
/// </summary>
public static class UpdateUrl
{
    /// <summary>Whether <paramref name="url"/> is an absolute https URL, or an http URL on localhost, 127.0.0.1 or [::1].</summary>
    public static bool IsAllowed(Uri? url) => UpdateRules.IsAllowedUrl(url);

    /// <summary>Parses <paramref name="text"/> as an allowed URL.</summary>
    public static bool TryParse(string? text, out Uri url)
    {
        if (Uri.TryCreate(text, UriKind.Absolute, out var parsed) && IsAllowed(parsed))
        {
            url = parsed;
            return true;
        }

        url = null!;
        return false;
    }

    /// <summary>Throws unless <paramref name="url"/> is allowed.</summary>
    /// <exception cref="UpdateException">The URL is not allowed (<see cref="UpdateErrorKind.InsecureUrl"/>).</exception>
    public static void EnsureAllowed(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!IsAllowed(url))
        {
            throw new UpdateException(
                UpdateErrorKind.InsecureUrl,
                $"'{url}' is not an https URL (plain http is accepted only for localhost)");
        }
    }
}
