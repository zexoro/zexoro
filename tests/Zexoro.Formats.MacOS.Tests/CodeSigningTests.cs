namespace Zexoro.Formats.MacOS.Tests;

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class CodeSigningTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("zexoro-codesign-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Bundle => Path.Combine(_dir.FullName, "Notes.app");

    private void Write(string relative, byte[] content)
    {
        var path = Path.Combine(Bundle, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private void CreateBundle()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Bundles keep symbolic links.");
        var macho = FatMachOTests.Thin(FatMachO.CpuTypeArm64, 0);
        Write("Contents/MacOS/Notes", macho);
        Write("Contents/MacOS/zexoro-updater", macho);
        Write("Contents/MacOS/libz.dylib", macho);
        Write("Contents/Resources/app/Notes.dll", "MZ"u8.ToArray());
        File.CreateSymbolicLink(Path.Combine(Bundle, "Contents/MacOS/Notes.dll"), "../Resources/app/Notes.dll");
        Write("Contents/Frameworks/Lib.framework/Versions/A/Lib", macho);
        File.CreateSymbolicLink(Path.Combine(Bundle, "Contents/Frameworks/Lib.framework/Lib"), "Versions/A/Lib");
        Write("Contents/Info.plist", "<plist/>"u8.ToArray());
    }

    [Fact]
    public void FindNestedCode_ListsMachOAndNestedBundlesDeepestFirst()
    {
        CreateBundle();

        Assert.Equal(
            [
                "Contents/Frameworks/Lib.framework/Versions/A/Lib",
                "Contents/Frameworks/Lib.framework",
                "Contents/MacOS/libz.dylib",
                "Contents/MacOS/zexoro-updater",
            ],
            MacCodeSigner.FindNestedCode(Bundle, "Contents/MacOS/Notes"));
    }

    [Fact]
    public void Sign_WithIdentity_SignsInsideOutWithHardenedRuntimeAndEntitlementsOnTheBundle()
    {
        CreateBundle();
        var tools = new FakeTools();

        MacCodeSigner.Sign(tools, Bundle, "Contents/MacOS/Notes", new MacSigningIdentity("Developer ID Application: X (T)", "/k/build.keychain", "/e.plist"), null);

        Assert.All(tools.Calls, c => Assert.Equal(MacCodeSigner.Codesign, c.File));
        Assert.Equal(6, tools.Calls.Count);
        Assert.Equal(
            ["--force", "--sign", "Developer ID Application: X (T)", "--options", "runtime", "--timestamp", "--keychain", "/k/build.keychain",
                Path.Combine(Bundle, "Contents/Frameworks/Lib.framework/Versions/A/Lib")],
            tools.Calls[0].Arguments);
        Assert.Equal(Path.Combine(Bundle, "Contents/MacOS/zexoro-updater"), tools.Calls[3].Arguments[^1]);
        Assert.Equal(
            ["--force", "--sign", "Developer ID Application: X (T)", "--options", "runtime", "--timestamp", "--keychain", "/k/build.keychain",
                "--entitlements", "/e.plist", Bundle],
            tools.Calls[4].Arguments);
        Assert.Equal(["--verify", "--deep", "--strict", "--verbose=2", Bundle], tools.Calls[5].Arguments);
    }

    [Fact]
    public void Sign_WithoutIdentity_SignsAdHocWithoutHardenedRuntime()
    {
        CreateBundle();
        var tools = new FakeTools();

        MacCodeSigner.Sign(tools, Bundle, "Contents/MacOS/Notes", null, null);

        Assert.Equal(["--force", "--sign", "-", Bundle], tools.Calls[4].Arguments);
        Assert.DoesNotContain(tools.Calls, c => c.Arguments.Contains("runtime"));
    }

    [Fact]
    public void Sign_RetriesTimestampFailuresOnly()
    {
        CreateBundle();
        MacCodeSigner.TimestampRetryDelay = TimeSpan.Zero;
        var failures = 2;
        var tools = new FakeTools((_, arguments) => arguments[^1] == Bundle && arguments[0] == "--force" && failures-- > 0
            ? new ToolResult(1, string.Empty, "Notes.app: The timestamp service is not available.")
            : new ToolResult(0, string.Empty, string.Empty));

        MacCodeSigner.Sign(tools, Bundle, "Contents/MacOS/Notes", new MacSigningIdentity("X", null, null), null);

        Assert.Equal(3, tools.Calls.Count(c => c.Arguments[0] == "--force" && c.Arguments[^1] == Bundle));

        var failing = new FakeTools((_, _) => new ToolResult(1, string.Empty, "error: The specified item could not be found in the keychain."));
        var error = Assert.Throws<MacPackException>(() => MacCodeSigner.Sign(failing, Bundle, "Contents/MacOS/Notes", new MacSigningIdentity("X", null, null), null));
        Assert.Single(failing.Calls);
        Assert.Contains("could not be found in the keychain", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_FailureIsReported()
    {
        var tools = new FakeTools((_, _) => new ToolResult(1, string.Empty, "Notes.app: a sealed resource is missing or invalid"));

        var error = Assert.Throws<MacPackException>(() => MacCodeSigner.Verify(tools, "/x/Notes.app"));

        Assert.Contains("a sealed resource is missing or invalid (exit code 1)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Notarize_SubmitsWaitsAndStaples()
    {
        var key = new MacNotarizationKey("/keys/AuthKey_ABC.p8", "ABC", "issuer-1");
        var tools = new FakeTools((_, arguments) => arguments[0] == "notarytool"
            ? new ToolResult(0, """{"id":"5c1e","status":"Accepted","message":"Processing complete"}""", string.Empty)
            : new ToolResult(0, "The staple and validate action worked!", string.Empty));

        MacNotarizer.Notarize(tools, "/out/n.zip", "/out/Notes.app", key, null);

        Assert.Equal(2, tools.Calls.Count);
        Assert.Equal(MacCodeSigner.Xcrun, tools.Calls[0].File);
        Assert.Equal(
            ["notarytool", "submit", "/out/n.zip", "--key", "/keys/AuthKey_ABC.p8", "--key-id", "ABC", "--issuer", "issuer-1", "--wait", "--output-format", "json"],
            tools.Calls[0].Arguments);
        Assert.Equal(["stapler", "staple", "/out/Notes.app"], tools.Calls[1].Arguments);
    }

    [Fact]
    public void Notarize_Rejected_FetchesTheLogAndFails()
    {
        var key = new MacNotarizationKey("/k.p8", "ABC", "issuer-1");
        var tools = new FakeTools((_, arguments) => arguments[1] switch
        {
            "submit" => new ToolResult(0, """{"id":"5c1e","status":"Invalid","message":"Processing complete"}""", string.Empty),
            "log" => new ToolResult(0, """{"issues":[{"message":"The binary is not signed with a valid Developer ID certificate."}]}""", string.Empty),
            _ => new ToolResult(0, string.Empty, string.Empty),
        });

        var error = Assert.Throws<MacPackException>(() => MacNotarizer.Notarize(tools, "/n.zip", "/Notes.app", key, null));

        Assert.Contains("status Invalid", error.Message, StringComparison.Ordinal);
        Assert.Contains("not signed with a valid Developer ID certificate", error.Message, StringComparison.Ordinal);
        Assert.Equal(["notarytool", "log", "5c1e", "--key", "/k.p8", "--key-id", "ABC", "--issuer", "issuer-1"], tools.Calls[1].Arguments);
        Assert.DoesNotContain(tools.Calls, c => c.Arguments[0] == "stapler");
    }

    [Fact]
    public void Run_MissingTool_IsAPackError()
    {
        var tools = new FakeTools((_, _) => throw new System.ComponentModel.Win32Exception(2, "No such file or directory"));

        var error = Assert.Throws<MacPackException>(() => MacCodeSigner.Verify(tools, "/Notes.app"));

        Assert.Contains("Cannot run /usr/bin/codesign", error.Message, StringComparison.Ordinal);
    }
}
