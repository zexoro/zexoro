namespace Zexoro.Formats.Linux.Tests;

public sealed class DesktopEntryTests
{
    [Fact]
    public void Create_WritesTheApplicationEntry()
    {
        var text = DesktopEntry.Create("Example Notes", "Takes notes", ["/opt/notes/app/notes"], "com.example.notes", ["Office", "Utility"]);

        Assert.Equal(
            "[Desktop Entry]\nType=Application\nVersion=1.5\nName=Example Notes\nComment=Takes notes\nExec=/opt/notes/app/notes\n"
            + "Icon=com.example.notes\nTerminal=false\nCategories=Office;Utility;\n",
            text);
    }

    [Fact]
    public void Create_WithoutCommentAndCategories_OmitsThem()
    {
        var text = DesktopEntry.Create("Notes", null, ["notes"], "notes", []);

        Assert.DoesNotContain("Comment=", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Categories=", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/opt/my app/run", "\"/opt/my app/run\"")]
    [InlineData("100%", "100%%")]
    [InlineData("a\"b$c`d\\e", "\"a\\\"b\\$c\\`d\\\\e\"")]
    [InlineData("", "\"\"")]
    [InlineData("plain-arg", "plain-arg")]
    public void QuoteArgument_QuotesReservedCharacters(string argument, string expected) =>
        Assert.Equal(expected, DesktopEntry.QuoteArgument(argument));

    [Fact]
    public void Create_ExecValue_IsStringEscapedAfterQuoting()
    {
        var text = DesktopEntry.Create("Notes", null, ["/opt/a\\b/notes", "--flag"], "notes", []);

        // Quoting turns \ into \\, string escaping doubles both again.
        Assert.Contains("Exec=\"/opt/a\\\\\\\\b/notes\" --flag\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EscapeString_EscapesControlCharactersAndALeadingSpace() =>
        Assert.Equal(@"\sa\nb\tc\rd\\e f", DesktopEntry.EscapeString(" a\nb\tc\rd\\e f"));
}
