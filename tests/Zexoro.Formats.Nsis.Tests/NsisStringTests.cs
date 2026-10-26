namespace Zexoro.Formats.Nsis.Tests;

public sealed class NsisStringTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a $b", "a $$b")]
    [InlineData("${DEFINE}", "$${DEFINE}")]
    [InlineData("$(lang)", "$$(lang)")]
    [InlineData("say \"hi\"", "say $\\\"hi$\\\"")]
    [InlineData("a\r\nb\tc", "a$\\r$\\nb$\\tc")]
    [InlineData("back\\slash\\", "back\\slash\\")]
    [InlineData("'single' `back`", "'single' `back`")]
    [InlineData("Ünïcødé 日本", "Ünïcødé 日本")]
    public void Escape_ProducesLiteralText(string input, string expected) =>
        Assert.Equal(expected, NsisString.Escape(input));

    [Fact]
    public void Escape_Nul_Throws() =>
        Assert.Throws<ArgumentException>(() => NsisString.Escape("a\0b"));

    [Fact]
    public void Quote_WrapsInDoubleQuotes() =>
        Assert.Equal("\"a$$\"", NsisString.Quote("a$"));

    [Fact]
    public void EscapePath_UsesBackslashes() =>
        Assert.Equal("bin\\My $$App.exe", NsisString.EscapePath("bin/My $App.exe"));
}
