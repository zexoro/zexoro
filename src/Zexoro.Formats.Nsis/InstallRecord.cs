using System.Text.Json;

namespace Zexoro.Formats.Nsis;

/// <summary>
/// Writes <c>zexoro-install.json</c> (docs/design/0002-update-contract.md section 6). Everything in it
/// is known when the installer is built, so the installer installs it as a plain file. With
/// <c>[updates]</c> it also holds <c>channel</c>, <c>feed</c>, <c>keys</c> and <c>main</c>
/// (docs/design/0005-nsis-updates.md section 3).
/// </summary>
public static class InstallRecord
{
    public const int Schema = 1;

    /// <summary>Returns the record for <paramref name="spec"/> as indented UTF-8 JSON with a final newline.</summary>
    public static byte[] Create(NsisInstallerSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", Schema);
            writer.WriteString("app", spec.AppId);
            writer.WriteString("version", spec.Version.ToString());
            writer.WriteString("os", "windows");
            writer.WriteString("arch", spec.Architecture.Name);
            writer.WriteString("format", "nsis");
            writer.WriteString("scope", spec.ScopeName);
            writer.WriteString("strategy", spec.Strategy);
            if (spec.Updates is { } updates)
            {
                writer.WriteString("channel", updates.Channel);
                writer.WriteString("feed", updates.Feed.AbsoluteUri);
                writer.WriteStartArray("keys");
                foreach (var key in updates.Keys)
                {
                    writer.WriteStringValue(key);
                }

                writer.WriteEndArray();
                writer.WriteString("main", spec.MainExecutable);
            }

            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }
}
