using System.Text;

namespace Zexoro.Formats.Linux;

/// <summary>
/// Writes <c>.desktop</c> files (freedesktop.org Desktop Entry Specification 1.5) for Linux formats
/// that install a launcher (docs/design/0006-desktop-formats.md sections 2 and 3).
/// </summary>
public static class DesktopEntry
{
    /// <summary>The specification version the files conform to.</summary>
    public const string SpecificationVersion = "1.5";

    /// <summary>Characters that make an <c>Exec</c> argument need quotes (specification, "The Exec key").</summary>
    private const string Reserved = " \t\n\"'\\><~|&;$*?#()`";

    /// <summary>
    /// Returns a <c>[Desktop Entry]</c> of type <c>Application</c> as UTF-8 text with <c>\n</c> line
    /// endings.
    /// </summary>
    /// <param name="name">The application name (<c>Name</c>).</param>
    /// <param name="comment">A description (<c>Comment</c>), or null.</param>
    /// <param name="exec">The program (an absolute path or a name on <c>PATH</c>) and its arguments; quoted and escaped as needed.</param>
    /// <param name="icon">An icon theme name or an absolute path (<c>Icon</c>).</param>
    /// <param name="categories">The <c>Categories</c>, already validated; none writes no <c>Categories</c> key.</param>
    public static string Create(string name, string? comment, IReadOnlyList<string> exec, string icon, IReadOnlyList<string> categories)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(exec);
        ArgumentException.ThrowIfNullOrEmpty(icon);
        ArgumentNullException.ThrowIfNull(categories);
        if (exec.Count == 0 || exec[0].Length == 0)
        {
            throw new ArgumentException("The command must name a program.", nameof(exec));
        }

        var text = new StringBuilder();
        text.Append("[Desktop Entry]\n");
        text.Append("Type=Application\n");
        text.Append("Version=").Append(SpecificationVersion).Append('\n');
        text.Append("Name=").Append(EscapeString(name)).Append('\n');
        if (!string.IsNullOrWhiteSpace(comment))
        {
            text.Append("Comment=").Append(EscapeString(comment)).Append('\n');
        }

        text.Append("Exec=").Append(EscapeString(string.Join(' ', exec.Select(QuoteArgument)))).Append('\n');
        text.Append("Icon=").Append(EscapeString(icon)).Append('\n');
        text.Append("Terminal=false\n");
        if (categories.Count > 0)
        {
            text.Append("Categories=").Append(string.Concat(categories.Select(c => c + ";"))).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Quotes one <c>Exec</c> argument: a literal '%' is doubled (it starts field codes), and an
    /// argument with a reserved character is put in double quotes with '"', '`', '$' and '\'
    /// escaped by a backslash.
    /// </summary>
    internal static string QuoteArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        var value = argument.Replace("%", "%%", StringComparison.Ordinal);
        if (value.Length > 0 && !value.Any(c => Reserved.Contains(c, StringComparison.Ordinal)))
        {
            return value;
        }

        var quoted = new StringBuilder("\"");
        foreach (var c in value)
        {
            if (c is '"' or '`' or '$' or '\\')
            {
                quoted.Append('\\');
            }

            quoted.Append(c);
        }

        return quoted.Append('"').ToString();
    }

    /// <summary>Escapes a value of type string: backslash, newline, tab, carriage return and a leading space.</summary>
    internal static string EscapeString(string value)
    {
        var escaped = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            escaped.Append(value[i] switch
            {
                '\\' => @"\\",
                '\n' => @"\n",
                '\t' => @"\t",
                '\r' => @"\r",
                ' ' when i == 0 => @"\s",
                var c => c.ToString(),
            });
        }

        return escaped.ToString();
    }
}
