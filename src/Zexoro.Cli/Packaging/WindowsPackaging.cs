using Zexoro.Configuration;
using Zexoro.Formats.Nsis;
using Zexoro.Platforms;
using Zexoro.Signing;
using Zexoro.Updates;

namespace Zexoro.Cli.Packaging;

/// <summary>What the Windows packagers share: loading the certificate and writing release fragments.</summary>
internal static class WindowsPackaging
{
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

    /// <summary>Writes the release fragment of a Windows artifact (see <see cref="ReleaseFragments.Write"/>).</summary>
    /// <exception cref="PackException">It cannot be written.</exception>
    public static void WriteFragment(PackContext context, UpdatesConfig updates, string artifact, Architecture architecture, string format, string scope, string minimumUpdater) =>
        ReleaseFragments.Write(context, updates, artifact, new UpdateTarget(OperatingSystemFamily.Windows.Name, architecture.Name, format, scope), minimumUpdater);
}
