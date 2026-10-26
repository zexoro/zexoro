using System.Text;
using Zexoro.Versioning;

namespace Zexoro.Updates.Tests;

internal static class TestFeeds
{
    public const string App = "com.example.notes";
    public const string Channel = "stable";

    public static readonly UpdateTarget UserX64 = new("windows", "x64", "nsis", "user");
    public static readonly UpdateTarget MachineX64 = new("windows", "x64", "nsis", "machine");
    public static readonly DateTimeOffset Published = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    public static SemanticVersion V(string text) => SemanticVersion.Parse(text);

    public static FeedArtifact Artifact(UpdateTarget? target = null, string? sha256 = null, string file = "app.exe", long size = 100) =>
        new(target ?? UserX64, new Uri($"https://example.com/dl/{file}"), size, sha256 ?? new string('a', 64));

    public static FeedRelease Release(string version, string? minimumUpdater = null, params FeedArtifact[] artifacts) =>
        new(V(version), artifacts.Length == 0 ? [Artifact(file: $"app-{version}.exe")] : artifacts, $"https://example.com/notes/{version}", minimumUpdater is null ? null : V(minimumUpdater));

    public static UpdateFeed Feed(long sequence, params FeedRelease[] releases) =>
        new(App, Channel, sequence, Published, releases);

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    public const string SampleJson = """
        {
          "schema": 1,
          "app": "com.example.notes",
          "channel": "stable",
          "sequence": 42,
          "published": "2026-10-26T12:00:00Z",
          "releases": [
            {
              "version": "1.2.3",
              "notes": "https://example.com/notes/1.2.3",
              "minimumUpdater": "0.1.0",
              "artifacts": [
                {
                  "os": "windows", "arch": "x64", "format": "nsis", "scope": "user",
                  "url": "https://example.com/dl/ExampleNotes-1.2.3-win-x64.exe",
                  "size": 48213112,
                  "sha256": "9F2C000000000000000000000000000000000000000000000000000000000001"
                }
              ]
            }
          ]
        }
        """;
}
