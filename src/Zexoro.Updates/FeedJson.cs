using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Zexoro.Versioning;

namespace Zexoro.Updates;

/// <summary>Reads and writes feeds and release fragments with source-generated System.Text.Json.</summary>
internal static class FeedJson
{
    private const string PublishedFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, NewLine = "\n" };

    public static UpdateFeed ParseFeed(ReadOnlySpan<byte> json)
    {
        CheckSchema(json, "feed");
        var dto = Deserialize(json, FeedJsonContext.Default.FeedDto, "feed");
        var app = Require(dto.App, "app");
        var channel = Require(dto.Channel, "channel");
        if (dto.Sequence is not { } sequence)
        {
            throw FeedRules.Invalid("feed: 'sequence' is missing");
        }

        var published = ParsePublished(Require(dto.Published, "published"));
        var releases = new List<FeedRelease>();
        var index = 0;
        foreach (var release in dto.Releases ?? throw FeedRules.Invalid("feed: 'releases' is missing"))
        {
            releases.Add(Prefix($"releases[{index++}]", () => ToRelease(release)));
        }

        return Prefix("feed", () => new UpdateFeed(app, channel, sequence, published, releases));
    }

    public static ReleaseFragment ParseFragment(ReadOnlySpan<byte> json)
    {
        CheckSchema(json, "release fragment");
        var dto = Deserialize(json, FeedJsonContext.Default.FragmentDto, "release fragment");
        var app = Require(dto.App, "app");
        var channel = Require(dto.Channel, "channel");
        var release = Prefix("release", () => ToRelease(dto.Release ?? throw FeedRules.Invalid("'release' is missing")));
        return Prefix("release fragment", () => new ReleaseFragment(app, channel, release));
    }

    public static byte[] Write(UpdateFeed feed) => Write(
        new FeedDto
        {
            Schema = UpdateFeed.SupportedSchema,
            App = feed.App,
            Channel = feed.Channel,
            Sequence = feed.Sequence,
            Published = feed.Published.UtcDateTime.ToString(PublishedFormat, CultureInfo.InvariantCulture),
            Releases = [.. feed.Releases.Select(ToDto)],
        },
        FeedJsonContext.Default.FeedDto);

    public static byte[] Write(ReleaseFragment fragment) => Write(
        new FragmentDto
        {
            Schema = ReleaseFragment.SupportedSchema,
            App = fragment.App,
            Channel = fragment.Channel,
            Release = ToDto(fragment.Release),
        },
        FeedJsonContext.Default.FragmentDto);

    private static byte[] Write<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            JsonSerializer.Serialize(writer, value, info);
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>Rejects a document whose <c>schema</c> is missing, not a positive integer, or newer than supported.</summary>
    private static void CheckSchema(ReadOnlySpan<byte> json, string what)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw FeedRules.Invalid($"{what}: expected a JSON object");
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isSchema = reader.ValueTextEquals("schema"u8);
                reader.Read();
                if (!isSchema)
                {
                    reader.Skip();
                    continue;
                }

                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var schema) || schema < 1)
                {
                    throw FeedRules.Invalid($"{what}: 'schema' must be a positive integer");
                }

                if (schema > UpdateFeed.SupportedSchema)
                {
                    throw new UpdateException(
                        UpdateErrorKind.UpdaterTooOld,
                        $"{what} schema {schema} is newer than this version understands ({UpdateFeed.SupportedSchema}); update manually to a newer version");
                }

                return;
            }
        }
        catch (JsonException ex)
        {
            throw new UpdateException(UpdateErrorKind.InvalidFeed, $"{what}: not valid JSON: {ex.Message}", ex);
        }

        throw FeedRules.Invalid($"{what}: 'schema' is missing");
    }

    private static T Deserialize<T>(ReadOnlySpan<byte> json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, string what)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, info) ?? throw FeedRules.Invalid($"{what}: expected a JSON object");
        }
        catch (JsonException ex)
        {
            throw new UpdateException(UpdateErrorKind.InvalidFeed, $"{what}: not valid: {ex.Message}", ex);
        }
    }

    private static FeedRelease ToRelease(ReleaseDto dto)
    {
        var version = ParseVersion(Require(dto.Version, "version"), "version");
        var minimum = dto.MinimumUpdater is null ? null : ParseVersion(dto.MinimumUpdater, "minimumUpdater");
        var artifacts = new List<FeedArtifact>();
        var index = 0;
        foreach (var artifact in dto.Artifacts ?? throw FeedRules.Invalid("'artifacts' is missing"))
        {
            artifacts.Add(Prefix($"artifacts[{index++}]", () => ToArtifact(artifact)));
        }

        return new FeedRelease(version, artifacts, dto.Notes, minimum);
    }

    private static FeedArtifact ToArtifact(ArtifactDto dto)
    {
        var url = Require(dto.Url, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw FeedRules.Invalid($"'{url}' is not an absolute http or https URL");
        }

        return new FeedArtifact(
            new UpdateTarget(Require(dto.Os, "os"), Require(dto.Arch, "arch"), Require(dto.Format, "format"), Require(dto.Scope, "scope")),
            uri,
            dto.Size ?? throw FeedRules.Invalid("'size' is missing"),
            Require(dto.Sha256, "sha256"));
    }

    private static ReleaseDto ToDto(FeedRelease release) => new()
    {
        Version = release.Version.ToString(),
        Notes = release.Notes,
        MinimumUpdater = release.MinimumUpdater?.ToString(),
        Artifacts =
        [
            .. release.Artifacts.Select(a => new ArtifactDto
            {
                Os = a.Target.Os,
                Arch = a.Target.Arch,
                Format = a.Target.Format,
                Scope = a.Target.Scope,
                Url = a.Url.AbsoluteUri,
                Size = a.Size,
                Sha256 = a.Sha256,
            }),
        ],
    };

    private static SemanticVersion ParseVersion(string text, string name) =>
        SemanticVersion.TryParse(text, out var version) ? version : throw FeedRules.Invalid($"'{name}' '{text}' is not a Semantic Versioning 2.0.0 version");

    private static DateTimeOffset ParsePublished(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) && text.Contains('T', StringComparison.Ordinal)
            ? value
            : throw FeedRules.Invalid($"'published' '{text}' is not an ISO 8601 date and time");

    private static string Require(string? value, string name) =>
        string.IsNullOrEmpty(value) ? throw FeedRules.Invalid($"'{name}' is missing") : value;

    private static T Prefix<T>(string context, Func<T> create)
    {
        try
        {
            return create();
        }
        catch (UpdateException ex)
        {
            throw new UpdateException(ex.Kind, $"{context}: {ex.Message}", ex);
        }
    }
}

internal sealed class FeedDto
{
    public long? Schema { get; set; }

    public string? App { get; set; }

    public string? Channel { get; set; }

    public long? Sequence { get; set; }

    public string? Published { get; set; }

    public List<ReleaseDto>? Releases { get; set; }
}

internal sealed class FragmentDto
{
    public long? Schema { get; set; }

    public string? App { get; set; }

    public string? Channel { get; set; }

    public ReleaseDto? Release { get; set; }
}

internal sealed class ReleaseDto
{
    public string? Version { get; set; }

    public string? Notes { get; set; }

    public string? MinimumUpdater { get; set; }

    public List<ArtifactDto>? Artifacts { get; set; }
}

internal sealed class ArtifactDto
{
    public string? Os { get; set; }

    public string? Arch { get; set; }

    public string? Format { get; set; }

    public string? Scope { get; set; }

    public string? Url { get; set; }

    public long? Size { get; set; }

    public string? Sha256 { get; set; }
}

/// <summary>
/// Unknown properties are ignored (later schema additions stay readable); duplicate properties are
/// rejected, so one signed document cannot mean two things to two readers.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowDuplicateProperties = false,
    MaxDepth = 16)]
[JsonSerializable(typeof(FeedDto))]
[JsonSerializable(typeof(FragmentDto))]
internal sealed partial class FeedJsonContext : JsonSerializerContext;
