using System.Text.Json;
using Zexoro.Updates;
using Zexoro.Versioning;

namespace Zexoro.Updater;

/// <summary>
/// The updater's per-user state for one application: <c>%LOCALAPPDATA%\zexoro\updater\&lt;app.id&gt;\</c>
/// with <c>state.json</c> (accepted feed sequences), <c>downloads\</c> (the verified artifact and
/// <c>staged.json</c> describing it) and <c>last-apply.json</c> (the result of the last apply).
/// </summary>
internal sealed class UpdaterState
{
    public UpdaterState(string localAppData, string app)
    {
        Directory = Path.Combine(localAppData, "zexoro", "updater", app);
    }

    public string Directory { get; }

    public string SequenceFile => Path.Combine(Directory, "state.json");

    public string Downloads => Path.Combine(Directory, "downloads");

    public string StagedFile => Path.Combine(Downloads, "staged.json");

    public string LastApplyFile => Path.Combine(Directory, "last-apply.json");

    /// <summary>Returns the staged update, or null when none is staged or its description is unreadable.</summary>
    public StagedUpdate? ReadStaged()
    {
        try
        {
            return StagedUpdate.Parse(File.ReadAllBytes(StagedFile), Downloads);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidDataException)
        {
            return null;
        }
    }

    public void WriteStaged(StagedUpdate staged) => WriteAtomically(StagedFile, staged.ToJson());

    /// <summary>Deletes the downloads directory, best effort.</summary>
    public void ClearDownloads()
    {
        try
        {
            if (System.IO.Directory.Exists(Downloads))
            {
                System.IO.Directory.Delete(Downloads, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked file is removed by the next download.
        }
    }

    public static void WriteAtomically(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)!;
        System.IO.Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}

/// <summary>A downloaded, verified installer waiting for <c>apply</c>.</summary>
internal sealed record StagedUpdate(string App, string Channel, SemanticVersion Version, string? Notes, string FileName, long Size, string Sha256, Uri Url)
{
    /// <summary>The installer's full path, given the downloads directory.</summary>
    public string PathIn(string downloads) => Path.Combine(downloads, FileName);

    /// <summary>The feed artifact the file was verified against.</summary>
    public FeedArtifact ToArtifact(UpdateTarget target) => new(target, Url, Size, Sha256);

    public byte[] ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", 1);
            writer.WriteString("app", App);
            writer.WriteString("channel", Channel);
            writer.WriteString("version", Version.ToString());
            if (Notes is not null)
            {
                writer.WriteString("notes", Notes);
            }

            writer.WriteString("file", FileName);
            writer.WriteNumber("size", Size);
            writer.WriteString("sha256", Sha256);
            writer.WriteString("url", Url.AbsoluteUri);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <exception cref="JsonException">Not JSON.</exception>
    /// <exception cref="InvalidDataException">A value is missing or invalid.</exception>
    public static StagedUpdate Parse(byte[] json, string downloads)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string Text(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                ? text
                : throw new InvalidDataException($"staged.json: '{name}' is missing");

        var file = Text("file");
        if (Path.GetFileName(file) != file || !File.Exists(Path.Combine(downloads, file)))
        {
            throw new InvalidDataException("staged.json: the staged file is missing");
        }

        var notes = root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        return new StagedUpdate(
            Text("app"),
            Text("channel"),
            SemanticVersion.TryParse(Text("version"), out var version) ? version : throw new InvalidDataException("staged.json: bad version"),
            notes,
            file,
            root.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : throw new InvalidDataException("staged.json: bad size"),
            Text("sha256"),
            new Uri(Text("url"), UriKind.Absolute));
    }
}
