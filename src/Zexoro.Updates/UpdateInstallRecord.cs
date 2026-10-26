using System.Text.Json;
using Zexoro.Versioning;

namespace Zexoro.Updates;

/// <summary>
/// <c>zexoro-install.json</c> as the updater reads it (docs/design/0002-update-contract.md section 6,
/// docs/design/0005-nsis-updates.md section 3). Unknown fields are ignored.
/// </summary>
public sealed class UpdateInstallRecord
{
    /// <summary>The file name, next to the updater in the install root.</summary>
    public const string FileName = "zexoro-install.json";

    /// <summary>The highest <c>schema</c> this version understands.</summary>
    public const int SupportedSchema = 1;

    private UpdateInstallRecord(string app, SemanticVersion version, UpdateTarget target, string strategy)
    {
        App = app;
        Version = version;
        Target = target;
        Strategy = strategy;
    }

    public string App { get; }

    public SemanticVersion Version { get; }

    /// <summary>The install's os, arch, format and scope.</summary>
    public UpdateTarget Target { get; }

    /// <summary>How the install is updated: <c>replace</c>, <c>reinstall</c>, …</summary>
    public string Strategy { get; }

    /// <summary>The channel, or null when the install was built without <c>[updates]</c>.</summary>
    public string? Channel { get; private init; }

    /// <summary>The expanded feed URL, or null when the install was built without <c>[updates]</c>.</summary>
    public Uri? Feed { get; private init; }

    /// <summary>The trusted feed keys.</summary>
    public IReadOnlyList<UpdatePublicKey> Keys { get; private init; } = [];

    /// <summary>The main executable, relative to <c>app\</c>, with forward slashes; null when not recorded.</summary>
    public string? Main { get; private init; }

    /// <summary>Reads the record at <paramref name="path"/>.</summary>
    /// <exception cref="UpdateException">It is missing or not valid (<see cref="UpdateErrorKind.InvalidInstallRecord"/>).</exception>
    public static UpdateInstallRecord Load(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw Invalid($"the install record {path} does not exist; the updater must run from the install directory", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Invalid($"cannot read the install record {path}: {ex.Message}", ex);
        }

        try
        {
            return Parse(bytes);
        }
        catch (UpdateException ex)
        {
            throw Invalid($"{path}: {ex.Message}", ex);
        }
    }

    /// <summary>Parses a record.</summary>
    /// <exception cref="UpdateException">It is not valid (<see cref="UpdateErrorKind.InvalidInstallRecord"/>).</exception>
    public static UpdateInstallRecord Parse(ReadOnlySpan<byte> json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray());
        }
        catch (JsonException ex)
        {
            throw Invalid($"the install record is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("the install record is not a JSON object");
            }

            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var schemaValue) || schemaValue < 1)
            {
                throw Invalid("the install record has no valid 'schema'");
            }

            if (schemaValue > SupportedSchema)
            {
                throw Invalid($"the install record has schema {schemaValue}; this updater understands {SupportedSchema}; update manually");
            }

            var app = RequireString(root, "app");
            if (!SemanticVersion.TryParse(RequireString(root, "version"), out var version))
            {
                throw Invalid("the install record's 'version' is not a semantic version");
            }

            var target = new UpdateTarget(RequireString(root, "os"), RequireString(root, "arch"), RequireString(root, "format"), RequireString(root, "scope"));
            var channel = OptionalString(root, "channel");
            if (channel is not null && !FeedRules.IsValidChannel(channel))
            {
                throw Invalid($"the install record's channel '{channel}' is not valid");
            }

            Uri? feed = null;
            if (OptionalString(root, "feed") is { } feedText && !UpdateUrl.TryParse(feedText, out feed))
            {
                throw Invalid($"the install record's feed '{feedText}' is not an https URL");
            }

            var keys = new List<UpdatePublicKey>();
            if (root.TryGetProperty("keys", out var keyArray))
            {
                if (keyArray.ValueKind != JsonValueKind.Array)
                {
                    throw Invalid("the install record's 'keys' is not an array");
                }

                foreach (var key in keyArray.EnumerateArray())
                {
                    if (key.ValueKind != JsonValueKind.String || !UpdatePublicKey.TryParse(key.GetString(), out var parsed))
                    {
                        throw Invalid("the install record lists a key that is not a valid update public key");
                    }

                    keys.Add(parsed);
                }
            }

            var main = OptionalString(root, "main");
            if (main is not null && !IsSafeRelativePath(main))
            {
                throw Invalid($"the install record's main '{main}' is not a relative path inside app\\");
            }

            return new UpdateInstallRecord(app, version, target, RequireString(root, "strategy"))
            {
                Channel = channel,
                Feed = feed,
                Keys = keys,
                Main = main,
            };
        }
    }

    /// <summary>The install as the update check sees it.</summary>
    /// <exception cref="UpdateException">The install was built without updates (<see cref="UpdateErrorKind.InvalidInstallRecord"/>).</exception>
    public InstalledApp ToInstalledApp()
    {
        if (Channel is null || Feed is null || Keys.Count == 0)
        {
            throw Invalid("this install was built without [updates]: its install record has no channel, feed or keys");
        }

        return new InstalledApp(App, Channel, Version, Target, Feed, Keys);
    }

    /// <summary>A relative path with forward or back slashes that stays inside its base directory.</summary>
    internal static bool IsSafeRelativePath(string path)
    {
        if (path.Length == 0 || Path.IsPathRooted(path) || path.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        return path.Split('/', '\\').All(segment => segment.Length > 0 && segment is not "." and not "..");
    }

    private static string RequireString(JsonElement root, string name) =>
        OptionalString(root, name) ?? throw Invalid($"the install record has no '{name}'");

    private static string? OptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : throw Invalid($"the install record's '{name}' is not a non-empty string");
    }

    private static UpdateException Invalid(string message, Exception? inner = null) =>
        inner is null
            ? new UpdateException(UpdateErrorKind.InvalidInstallRecord, message)
            : new UpdateException(UpdateErrorKind.InvalidInstallRecord, message, inner);
}
