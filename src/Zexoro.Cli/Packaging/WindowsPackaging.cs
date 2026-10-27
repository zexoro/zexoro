using Zexoro.Configuration;
using Zexoro.Formats.Nsis;
using Zexoro.Platforms;
using Zexoro.Signing;
using Zexoro.Updates;
using Zexoro.Versioning;

namespace Zexoro.Cli.Packaging;

/// <summary>What the Windows packagers share: loading the certificate and writing release fragments.</summary>
internal static class WindowsPackaging
{
    /// <summary>The release fragment's file name suffix: <c>&lt;artifact&gt;.release.json</c>.</summary>
    public const string FragmentSuffix = ".release.json";

    /// <summary>
    /// Loads <c>[windows.signing]</c>, or prints a notice and returns null when it is not configured.
    /// The caller disposes the credential.
    /// </summary>
    /// <exception cref="PackException">The certificate is missing or cannot be loaded.</exception>
    public static NsisSigning? LoadSigning(PackContext context, string unsignedNotice)
    {
        if (context.Config.Windows.Signing is not { } signing)
        {
            context.Log.WriteLine($"notice: [windows.signing] is not configured; {unsignedNotice}");
            return null;
        }

        var environment = context.Environment;
        var path = signing.ResolveCertificatePath(environment.GetVariable);
        var password = signing.ResolvePassword(environment.GetVariable);
        var timestamp = signing.ResolveTimestamp(environment.GetVariable);
        if (!File.Exists(path))
        {
            throw new PackException($"{signing.Certificate.Key}: certificate file {path} not found.");
        }

        SigningCredential credential;
        try
        {
            credential = SigningCredential.LoadPfx(path, password);
        }
        catch (SigningException ex)
        {
            throw new PackException($"{signing.Certificate.Key}: {ex.Message}", ex);
        }

        return new NsisSigning
        {
            Credential = credential,
            CertificatePath = path,
            Password = password,
            Timestamp = timestamp,
            ZexoroCommand = environment.ZexoroCommand,
        };
    }

    /// <summary>
    /// Writes <c>&lt;artifact&gt;.release.json</c> (docs/design/0005-nsis-updates.md section 2): the
    /// release with one artifact, the final (signed) file, for <c>zexoro feed add</c>; prints it.
    /// </summary>
    /// <exception cref="PackException">It cannot be written.</exception>
    public static void WriteFragment(PackContext context, UpdatesConfig updates, string artifact, Architecture architecture, string format, string scope, string minimumUpdater)
    {
        string path;
        try
        {
            path = WriteFragment(context.Config, updates, artifact, new UpdateTarget("windows", architecture.Name, format, scope), minimumUpdater, context.Notes);
        }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException)
        {
            throw new PackException($"cannot write the release fragment: {ex.Message}", ex);
        }

        context.Log.WriteLine($"Created {path} (channel {updates.Channel}); publish it with 'zexoro feed add'");
    }

    internal static string WriteFragment(ZexoroConfig config, UpdatesConfig updates, string artifact, UpdateTarget target, string minimumUpdater, string? notes)
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
        var path = artifact + FragmentSuffix;
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, new ReleaseFragment(config.App.Id, updates.Channel, release).ToJson());
        File.Move(temporary, path, overwrite: true);
        return path;
    }
}
