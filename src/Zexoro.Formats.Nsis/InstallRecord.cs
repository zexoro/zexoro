using System.Text.Json;

namespace Zexoro.Formats.Nsis;

/// <summary>
/// Writes <c>zexoro-install.json</c> (docs/design/0002-update-contract.md section 6). Everything in it
/// is known when the installer is built, so the installer installs it as a plain file. The update
/// fields (<c>channel</c>, <c>feed</c>, <c>keys</c>) are left out until updates are configurable.
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
            writer.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }
}
