using Zexoro.Configuration;
using Zexoro.Updates;
using Zexoro.Versioning;

namespace Zexoro.Cli.Packaging;

/// <summary>Release fragments: what every packager writes next to its artifact with <c>[updates]</c>.</summary>
internal static class ReleaseFragments
{
    /// <summary>The release fragment's file name suffix: <c>&lt;artifact&gt;.release.json</c>.</summary>
    public const string Suffix = ".release.json";

    /// <summary>
    /// Writes <c>&lt;artifact&gt;.release.json</c> (docs/design/0005-nsis-updates.md section 2): the
    /// release with one artifact, the final (signed) file, for <c>zexoro feed add</c>; prints it.
    /// </summary>
    /// <exception cref="PackException">It cannot be written.</exception>
    public static void Write(PackContext context, UpdatesConfig updates, string artifact, UpdateTarget target, string minimumUpdater)
    {
        string path;
        try
        {
            path = Write(context.Config, updates, artifact, target, minimumUpdater, context.Notes);
        }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException)
        {
            throw new PackException($"cannot write the release fragment: {ex.Message}", ex);
        }

        context.Log.WriteLine($"Created {path} (channel {updates.Channel}); publish it with 'zexoro feed add'");
    }

    internal static string Write(ZexoroConfig config, UpdatesConfig updates, string artifact, UpdateTarget target, string minimumUpdater, string? notes)
    {
        long size;
        string sha256;
        using (var stream = File.OpenRead(artifact))
        {
            size = stream.Length;
            sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
        }

        var url = updates.DownloadUrl(Path.GetFileName(artifact), config.App.Version.ToString());
        var release = new FeedRelease(config.App.Version, [new FeedArtifact(target, url, size, sha256)], notes, SemanticVersion.Parse(minimumUpdater));
        var path = artifact + Suffix;
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, new ReleaseFragment(config.App.Id, updates.Channel, release).ToJson());
        File.Move(temporary, path, overwrite: true);
        return path;
    }
}
