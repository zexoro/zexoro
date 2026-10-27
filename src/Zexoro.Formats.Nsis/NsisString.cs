using System.Text;

namespace Zexoro.Formats.Nsis;

/// <summary>Escapes text for NSIS script string literals.</summary>
public static class NsisString
{
    /// <summary>
    /// Escapes <paramref name="text"/> for use inside a double-quoted NSIS string, so it is taken
    /// literally at run time: <c>$</c> becomes <c>$$</c> (which also neutralizes <c>${define}</c>,
    /// <c>$(langstring)</c> and variables), <c>"</c> becomes <c>$\"</c>, and carriage return, line
    /// feed and tab become <c>$\r</c>, <c>$\n</c> and <c>$\t</c>, so a value never ends a line.
    /// </summary>
    /// <exception cref="ArgumentException">The text contains a NUL character, which NSIS strings cannot hold.</exception>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("NSIS strings cannot contain NUL characters.", nameof(text));
        }

        var builder = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '$' => "$$",
                '"' => "$\\\"",
                '\r' => "$\\r",
                '\n' => "$\\n",
                '\t' => "$\\t",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }

    /// <summary>Returns <paramref name="text"/> as a double-quoted NSIS string literal.</summary>
    public static string Quote(string text) => "\"" + Escape(text) + "\"";

    /// <summary>Converts a relative path with forward or back slashes to a Windows path fragment, escaped.</summary>
    public static string EscapePath(string relativePath) => Escape(relativePath.Replace('/', '\\'));
}
