using System.Net;
using System.Security.Cryptography;
using Zexoro.Versioning;

namespace Zexoro.Updates;

/// <summary>Fetches feeds and artifacts over HTTP(S), enforcing the transport, size and hash rules.</summary>
public sealed class UpdateClient
{
    /// <summary>The largest <c>feed.json</c> accepted.</summary>
    public const int MaxFeedSize = 16 * 1024 * 1024;

    private readonly HttpClient _http;

    /// <param name="http">
    /// The client to use. .NET's default handler never follows a redirect from https to http;
    /// every final URL is checked again anyway.
    /// </param>
    public UpdateClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    /// <summary>The signature URL for a feed URL: the same URL with <c>.sig</c> appended to its path.</summary>
    public static Uri SignatureUrl(Uri feedUrl)
    {
        ArgumentNullException.ThrowIfNull(feedUrl);
        var builder = new UriBuilder(feedUrl);
        builder.Path += ".sig";
        return builder.Uri;
    }

    /// <summary>Downloads the feed and its signature and runs <see cref="UpdateCheck.Evaluate"/>.</summary>
    /// <exception cref="UpdateException">Download or verification failed.</exception>
    public async Task<UpdateSelection> CheckAsync(
        InstalledApp installed, FeedSequenceStore sequences, SemanticVersion updaterVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var feed = await GetBytesAsync(installed.Feed, MaxFeedSize, cancellationToken).ConfigureAwait(false);
        var signature = await GetBytesAsync(SignatureUrl(installed.Feed), FeedSignature.MaxFileSize, cancellationToken).ConfigureAwait(false);
        return UpdateCheck.Evaluate(installed, feed, signature, sequences, updaterVersion);
    }

    /// <summary>
    /// Downloads <paramref name="artifact"/> to <paramref name="destination"/>. The bytes go to a
    /// temporary sibling while being hashed; the download stops as soon as it exceeds the declared
    /// size, and the file is moved into place only when size and SHA-256 match.
    /// </summary>
    /// <exception cref="UpdateException">The URL is not allowed, the download failed, or the file does not match.</exception>
    public async Task DownloadAsync(FeedArtifact artifact, string destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".partial";
        try
        {
            using (var response = await SendAsync(artifact.Url, cancellationToken).ConfigureAwait(false))
            {
                if (response.Content.Headers.ContentLength is { } length && length != artifact.Size)
                {
                    throw Mismatch(artifact, $"the server announces {length} bytes, the feed {artifact.Size}");
                }

                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
                    await using (file.ConfigureAwait(false))
                    {
                        await CopyVerifiedAsync(stream, file, artifact, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            File.Move(partial, destination, overwrite: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new UpdateException(UpdateErrorKind.Network, $"cannot download {artifact.Url}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException(UpdateErrorKind.Network, $"cannot download {artifact.Url}: the request timed out", ex);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    /// <summary>Checks that the file at <paramref name="path"/> has the artifact's size and SHA-256.</summary>
    /// <exception cref="UpdateException">It does not (<see cref="UpdateErrorKind.Integrity"/>).</exception>
    public static void VerifyFile(string path, FeedArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        using var stream = File.OpenRead(path);
        VerifyStream(stream, artifact);
    }

    /// <summary>Checks that <paramref name="stream"/>, read to its end, has the artifact's size and SHA-256.</summary>
    /// <exception cref="UpdateException">It does not (<see cref="UpdateErrorKind.Integrity"/>).</exception>
    public static void VerifyStream(Stream stream, FeedArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(artifact);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            total += read;
            if (total > artifact.Size)
            {
                throw Mismatch(artifact, $"it is larger than the {artifact.Size} bytes in the feed");
            }

            hash.AppendData(buffer, 0, read);
        }

        Finish(hash, total, artifact);
    }

    private static async Task CopyVerifiedAsync(Stream source, Stream destination, FeedArtifact artifact, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > artifact.Size)
            {
                throw Mismatch(artifact, $"it is larger than the {artifact.Size} bytes in the feed");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        Finish(hash, total, artifact);
    }

    private static void Finish(IncrementalHash hash, long total, FeedArtifact artifact)
    {
        if (total != artifact.Size)
        {
            throw Mismatch(artifact, $"it has {total} bytes, the feed says {artifact.Size}");
        }

        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actual, artifact.Sha256, StringComparison.Ordinal))
        {
            throw Mismatch(artifact, $"its SHA-256 is {actual}, the feed says {artifact.Sha256}");
        }
    }

    private static UpdateException Mismatch(FeedArtifact artifact, string detail) =>
        new(UpdateErrorKind.Integrity, $"{artifact.Url} does not match the feed: {detail}");

    private async Task<byte[]> GetBytesAsync(Uri url, int maxSize, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > maxSize)
            {
                throw new UpdateException(UpdateErrorKind.InvalidFeed, $"{url} is larger than {maxSize} bytes");
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[16384];
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > maxSize)
                    {
                        throw new UpdateException(UpdateErrorKind.InvalidFeed, $"{url} is larger than {maxSize} bytes");
                    }

                    buffer.Write(chunk, 0, read);
                }

                return buffer.ToArray();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new UpdateException(UpdateErrorKind.Network, $"cannot download {url}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException(UpdateErrorKind.Network, $"cannot download {url}: the request timed out", ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, CancellationToken cancellationToken)
    {
        UpdateUrl.EnsureAllowed(url);
        var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            var final = response.RequestMessage?.RequestUri ?? url;
            UpdateUrl.EnsureAllowed(final);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new UpdateException(UpdateErrorKind.Network, $"cannot download {url}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
