using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zexoro.Updates;

/// <summary>
/// The highest feed <c>sequence</c> accepted per application and channel, persisted in a small JSON
/// file. A validly signed feed with a lower sequence is a replay of an old feed and is rejected
/// (docs/design/0002-update-contract.md section 4).
/// </summary>
public sealed class FeedSequenceStore
{
    public FeedSequenceStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    /// <summary>The state file.</summary>
    public string Path { get; }

    /// <summary>Returns the highest accepted sequence, or null when none was accepted yet.</summary>
    /// <exception cref="InvalidDataException">The state file is damaged; deleting it resets replay protection.</exception>
    public long? GetSequence(string app, string channel)
    {
        var state = Read();
        return state.Sequences!.TryGetValue(Key(app, channel), out var value) ? value : null;
    }

    /// <summary>Throws when <paramref name="feed"/> is older than a feed already accepted.</summary>
    /// <exception cref="UpdateException">The feed's sequence is lower (<see cref="UpdateErrorKind.Replay"/>).</exception>
    /// <exception cref="InvalidDataException">The state file is damaged.</exception>
    public void Check(UpdateFeed feed)
    {
        ArgumentNullException.ThrowIfNull(feed);
        if (GetSequence(feed.App, feed.Channel) is { } seen && feed.Sequence < seen)
        {
            throw new UpdateException(
                UpdateErrorKind.Replay,
                $"the feed has sequence {feed.Sequence}, lower than the {seen} already seen; an old feed is being served");
        }
    }

    /// <summary>Checks <paramref name="feed"/> and records its sequence if it is the highest so far.</summary>
    /// <exception cref="UpdateException">The feed's sequence is lower (<see cref="UpdateErrorKind.Replay"/>).</exception>
    /// <exception cref="InvalidDataException">The state file is damaged.</exception>
    /// <exception cref="IOException">The state file cannot be written.</exception>
    public void Accept(UpdateFeed feed)
    {
        Check(feed);
        var state = Read();
        var key = Key(feed.App, feed.Channel);
        if (state.Sequences!.TryGetValue(key, out var seen) && seen >= feed.Sequence)
        {
            return;
        }

        state.Sequences[key] = feed.Sequence;
        Write(state);
    }

    private static string Key(string app, string channel) => $"{app}/{channel}";

    private SequenceState Read()
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(Path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new SequenceState { Sequences = new(StringComparer.Ordinal) };
        }

        try
        {
            var state = JsonSerializer.Deserialize(bytes, SequenceStateContext.Default.SequenceState);
            if (state is null || state.Schema != 1 || state.Sequences is null)
            {
                throw new InvalidDataException($"the update state file {Path} is damaged; delete it to reset");
            }

            return state;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"the update state file {Path} is damaged; delete it to reset", ex);
        }
    }

    /// <summary>Writes to a temporary sibling and renames it over the old file, so a crash never leaves a partial file.</summary>
    private void Write(SequenceState state)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(state, SequenceStateContext.Default.SequenceState));
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}

internal sealed class SequenceState
{
    public int Schema { get; set; } = 1;

    public Dictionary<string, long>? Sequences { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SequenceState))]
internal sealed partial class SequenceStateContext : JsonSerializerContext;
