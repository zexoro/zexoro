using System.Runtime.CompilerServices;
using System.Text;

namespace Zexoro.Formats.Nsis.Tests;

/// <summary>
/// Compares text with a file under <c>Snapshots/</c> in the source tree. Set
/// <c>ZEXORO_UPDATE_SNAPSHOTS=1</c> to rewrite the files, then review the diff.
/// </summary>
internal static class Snapshot
{
    public static void Match(string name, string actual, [CallerFilePath] string callerPath = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(callerPath)!, "Snapshots", name);
        var update = Environment.GetEnvironmentVariable("ZEXORO_UPDATE_SNAPSHOTS") == "1";
        if (update || !File.Exists(path))
        {
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            Assert.True(update, $"Snapshot {path} did not exist and was written; review it and run the tests again.");
            return;
        }

        var expected = File.ReadAllText(path, Encoding.UTF8).ReplaceLineEndings("\n");
        Assert.Equal(expected, actual);
    }
}
