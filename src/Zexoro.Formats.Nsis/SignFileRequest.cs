using System.Text.Json;

namespace Zexoro.Formats.Nsis;

/// <summary>
/// What the hidden <c>zexoro sign-file</c> command needs to sign a file that makensis produced
/// (the uninstaller and the installer). It is written to a JSON file in the private staging
/// directory; the certificate password never appears in it or on a command line, but is passed in
/// the <see cref="PasswordVariable"/> environment variable of makensis, which the command inherits.
/// </summary>
public sealed record SignFileRequest
{
    /// <summary>The environment variable that carries the certificate password to <c>sign-file</c>.</summary>
    public const string PasswordVariable = "ZEXORO_SIGN_FILE_PASSWORD";

    /// <summary>The absolute path of the PFX file.</summary>
    public required string Certificate { get; init; }

    /// <summary>The RFC 3161 timestamp authority, or null for no timestamp.</summary>
    public Uri? Timestamp { get; init; }

    /// <summary>The signature's program name.</summary>
    public string? Description { get; init; }

    /// <summary>The signature's program URL.</summary>
    public Uri? DescriptionUrl { get; init; }

    public void Write(string path)
    {
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("certificate", Certificate);
        WriteOptional(writer, "timestamp", Timestamp?.ToString());
        WriteOptional(writer, "description", Description);
        WriteOptional(writer, "descriptionUrl", DescriptionUrl?.ToString());
        writer.WriteEndObject();
    }

    /// <exception cref="InvalidDataException">The file is not a valid request.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static SignFileRequest Read(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            return new SignFileRequest
            {
                Certificate = root.GetProperty("certificate").GetString() ?? throw new InvalidDataException("certificate is null"),
                Timestamp = OptionalUri(root, "timestamp"),
                Description = root.TryGetProperty("description", out var d) ? d.GetString() : null,
                DescriptionUrl = OptionalUri(root, "descriptionUrl"),
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or UriFormatException)
        {
            throw new InvalidDataException($"{path} is not a valid signing request: {ex.Message}", ex);
        }
    }

    private static Uri? OptionalUri(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.GetString() is { } text ? new Uri(text, UriKind.Absolute) : null;

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }
}
