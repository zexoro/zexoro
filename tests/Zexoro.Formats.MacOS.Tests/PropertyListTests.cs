using System.Text;

namespace Zexoro.Formats.MacOS.Tests;

public sealed class PropertyListTests
{
    /// <summary>
    /// Written by <c>plutil -convert binary1</c> on macOS from an XML list with a string, a non-ASCII
    /// string (UTF-16), true, an integer, an array and data.
    /// </summary>
    private const string BinaryFixture =
        "YnBsaXN0MDDXAQIDBAUGBwgJCgsMDQ5fEBpDRkJ1bmRsZVNob3J0VmVyc2lvblN0cmluZ1RCbG9iXxASQ0ZCdW5kbGVJZGVudGlmaWVyW0xTVUlFbGVtZW50XENGQnVuZGxlTmFtZVVDb3VudFVBcmNoc1UxLjIuM0MBAgNfEBFjb20uZXhhbXBsZS5ub3RlcwlnAE4AbwB0AGUAcwAgJxMRASyiDxBWeDg2XzY0VWFybTY0CBc0OU5aZ21zeX2RkqGkp64AAAAAAAABAQAAAAAAAAARAAAAAAAAAAAAAAAAAAAAtA==";

    [Fact]
    public void ToXml_WritesSortedKeysInApplesLayout()
    {
        var xml = Encoding.UTF8.GetString(PropertyList.ToXml(new Dictionary<string, object>
        {
            ["CFBundleName"] = "A & B",
            ["CFBundleExecutable"] = "notes",
            ["NSHighResolutionCapable"] = true,
        }));

        Assert.Equal(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n"
            + "<plist version=\"1.0\">\n<dict>\n"
            + "\t<key>CFBundleExecutable</key>\n\t<string>notes</string>\n"
            + "\t<key>CFBundleName</key>\n\t<string>A &amp; B</string>\n"
            + "\t<key>NSHighResolutionCapable</key>\n\t<true/>\n"
            + "</dict>\n</plist>\n",
            xml);
    }

    [Fact]
    public void Read_RoundTripsXml()
    {
        var bytes = PropertyList.ToXml(new Dictionary<string, object>
        {
            ["s"] = "<x>",
            ["i"] = 42L,
            ["f"] = false,
            ["a"] = new List<object> { "one", 2L },
            ["d"] = new byte[] { 1, 2, 3 },
            ["n"] = new Dictionary<string, object> { ["k"] = "v" },
        });

        var plist = PropertyList.Read(bytes);

        Assert.Equal("<x>", plist["s"]);
        Assert.Equal(42L, plist["i"]);
        Assert.Equal(false, plist["f"]);
        Assert.Equal(new List<object> { "one", 2L }, plist["a"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, plist["d"]);
        Assert.Equal("v", ((Dictionary<string, object>)plist["n"])["k"]);
    }

    [Fact]
    public void Read_ReadsBinaryPropertyLists()
    {
        var plist = PropertyList.Read(Convert.FromBase64String(BinaryFixture));

        Assert.Equal("com.example.notes", plist["CFBundleIdentifier"]);
        Assert.Equal("1.2.3", plist["CFBundleShortVersionString"]);
        Assert.Equal("Notes ✓", plist["CFBundleName"]);
        Assert.Equal(true, plist["LSUIElement"]);
        Assert.Equal(300L, plist["Count"]);
        Assert.Equal(new List<object> { "x86_64", "arm64" }, plist["Archs"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, plist["Blob"]);
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("<?xml version=\"1.0\"?><array/>")]
    [InlineData("<plist version=\"1.0\"><array/></plist>")]
    [InlineData("<plist version=\"1.0\"><dict><string>x</string></dict></plist>")]
    public void Read_RejectsInvalidXml(string text)
    {
        Assert.Throws<PropertyListException>(() => PropertyList.Read(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Read_RejectsATruncatedBinaryList()
    {
        var bytes = Convert.FromBase64String(BinaryFixture);

        Assert.Throws<PropertyListException>(() => PropertyList.Read(bytes[..^40]));
    }
}
