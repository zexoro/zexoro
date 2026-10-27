using Zexoro.Cli.Toolsets;
using Zexoro.Configuration;
using Zexoro.Formats.Nsis;
using Zexoro.Platforms;
using Zexoro.Signing;
using Zexoro.Toolsets;
using Zexoro.Updates;
using Zexoro.Versioning;

namespace Zexoro.Cli.Packaging;

/// <summary>Windows <c>nsis</c>: an NSIS installer and, with <c>[updates]</c>, its release fragment.</summary>
internal sealed class NsisFormatPackager : IPackager
{
    /// <summary>The release fragment's file name suffix: <c>&lt;installer&gt;.release.json</c>.</summary>
    public const string FragmentSuffix = ".release.json";

    public PackageFormat Format => PackageFormat.Nsis;

    public void Pack(PackContext context)
    {
        var config = context.Config;
        var stdout = context.Log;
        NsisSigning? signing = null;
        try
        {
            if (config.Windows.Signing is { } signingConfig)
            {
                signing = LoadSigning(signingConfig, context.Environment);
            }
            else
            {
                stdout.WriteLine("notice: [windows.signing] is not configured; the installer and the application are not signed.");
            }

            var toolset = BundledToolsets.ResolveNsis(context.Environment);
            var result = NsisPackager.Pack(new NsisPackRequest
            {
                Config = config,
                Toolset = toolset,
                Architecture = context.Target.Architecture,
                OutputDirectory = context.OutputDirectory,
                Signing = signing,
                Log = stdout,
                ResolveUpdater = architecture => BundledUpdaters.Resolve(architecture, context.Environment),
            });
            stdout.WriteLine($"Created {result.InstallerPath}");
            if (config.Updates is { } updates)
            {
                string fragment;
                try
                {
                    fragment = WriteFragment(config, updates, result, context.Notes);
                }
                catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException)
                {
                    throw new PackException($"cannot write the release fragment: {ex.Message}", ex);
                }

                stdout.WriteLine($"Created {fragment} (channel {updates.Channel}); publish it with 'zexoro feed add'");
            }
        }
        catch (Exception ex) when (ex is NsisException or ToolsetException)
        {
            throw new PackException(ex.Message, ex);
        }
        finally
        {
            signing?.Credential.Dispose();
        }
    }

    /// <summary>
    /// Writes <c>&lt;installer&gt;.release.json</c> (docs/design/0005-nsis-updates.md section 2): the
    /// release with one artifact, the final (signed) installer, for <c>zexoro feed add</c>.
    /// </summary>
    internal static string WriteFragment(ZexoroConfig config, UpdatesConfig updates, NsisPackResult result, string? notes)
    {
        var installer = result.InstallerPath;
        long size;
        string sha256;
        using (var stream = File.OpenRead(installer))
        {
            size = stream.Length;
            sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
        }

        var spec = result.Spec;
        var target = new UpdateTarget("windows", spec.Architecture.Name, "nsis", spec.ScopeName);
        var url = updates.DownloadUrl(Path.GetFileName(installer), config.App.Version.ToString());
        var release = new FeedRelease(
            config.App.Version,
            [new FeedArtifact(target, url, size, sha256)],
            notes,
            SemanticVersion.Parse(NsisScript.MinimumUpdater));
        var path = installer + FragmentSuffix;
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, new ReleaseFragment(config.App.Id, updates.Channel, release).ToJson());
        File.Move(temporary, path, overwrite: true);
        return path;
    }

    private static NsisSigning LoadSigning(WindowsSigningConfig signing, CliEnvironment environment)
    {
        var path = signing.ResolveCertificatePath(environment.GetVariable);
        var password = signing.ResolvePassword(environment.GetVariable);
        var timestamp = signing.ResolveTimestamp(environment.GetVariable);
        if (!File.Exists(path))
        {
            throw new NsisException($"{signing.Certificate.Key}: certificate file {path} not found.");
        }

        SigningCredential credential;
        try
        {
            credential = SigningCredential.LoadPfx(path, password);
        }
        catch (SigningException ex)
        {
            throw new NsisException($"{signing.Certificate.Key}: {ex.Message}", ex);
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
}
