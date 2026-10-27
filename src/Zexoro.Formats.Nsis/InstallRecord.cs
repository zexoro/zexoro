using System.Text.Json;
using Zexoro.Platforms;
using Zexoro.Versioning;

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
        return Create(spec.AppId, spec.Version, spec.Architecture, "nsis", spec.ScopeName, spec.Strategy, spec.Updates, spec.MainExecutable);
    }

    /// <summary>
    /// Returns a record as indented UTF-8 JSON with a final newline, for any Windows format.
    /// <paramref name="main"/> is written only with <paramref name="updates"/>.
    /// </summary>
    public static byte[] Create(
        string appId, SemanticVersion version, Architecture architecture, string format, string scope, string strategy, NsisUpdates? updates, string main)
    {
        ArgumentNullException.ThrowIfNull(version);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema", Schema);
            writer.WriteString("app", appId);
            writer.WriteString("version", version.ToString());
            writer.WriteString("os", "windows");
            writer.WriteString("arch", architecture.Name);
            writer.WriteString("format", format);
            writer.WriteString("scope", scope);
            writer.WriteString("strategy", strategy);
            if (updates is not null)
            {
                writer.WriteString("channel", updates.Channel);
                writer.WriteString("feed", updates.Feed.AbsoluteUri);
                writer.WriteStartArray("keys");
                foreach (var key in updates.Keys)
                {
                    writer.WriteStringValue(key);
                }

                writer.WriteEndArray();
                writer.WriteString("main", main);
            }

            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }
}
