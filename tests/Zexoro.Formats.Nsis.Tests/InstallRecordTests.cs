using System.Text;
using System.Text.Json;
using Zexoro.Configuration;

namespace Zexoro.Formats.Nsis.Tests;

public sealed class InstallRecordTests
{
    [Fact]
    public void Create_UserScope_RecordsReplaceStrategy()
    {
        var json = Encoding.UTF8.GetString(InstallRecord.Create(NsisScriptTests.UserDefault));
        Assert.Equal(
            """
            {
              "schema": 1,
              "app": "com.example.notes",
              "version": "1.2.3",
              "os": "windows",
              "arch": "x64",
              "format": "nsis",
              "scope": "user",
              "strategy": "replace"
            }

            """.ReplaceLineEndings("\n"),
            json);
    }

    [Fact]
    public void Create_MachineScope_RecordsReinstallStrategy()
    {
        using var document = JsonDocument.Parse(InstallRecord.Create(NsisScriptTests.MachineFull with { Scope = WindowsInstallScope.Machine }));
        Assert.Equal("machine", document.RootElement.GetProperty("scope").GetString());
        Assert.Equal("reinstall", document.RootElement.GetProperty("strategy").GetString());
    }

    [Fact]
    public void Create_EscapesJson()
    {
        using var document = JsonDocument.Parse(InstallRecord.Create(NsisScriptTests.UserEscaped));
        Assert.Equal("2.0.0-beta.1+build.7", document.RootElement.GetProperty("version").GetString());
        Assert.Equal("arm64", document.RootElement.GetProperty("arch").GetString());
    }

    [Fact]
    public void Create_WithUpdates_RecordsChannelFeedKeysAndMain()
    {
        var json = Encoding.UTF8.GetString(InstallRecord.Create(NsisScriptTests.UserUpdates with { MainExecutable = "bin/Notes.exe" }));
        Assert.Equal(
            """
            {
              "schema": 1,
              "app": "com.example.notes",
              "version": "1.2.3",
              "os": "windows",
              "arch": "x64",
              "format": "nsis",
              "scope": "user",
              "strategy": "replace",
              "channel": "beta",
              "feed": "https://example.com/updates/beta/feed.json",
              "keys": [
                "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEa",
                "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEb"
              ],
              "main": "bin/Notes.exe"
            }

            """.ReplaceLineEndings("\n"),
            json);
    }
}
