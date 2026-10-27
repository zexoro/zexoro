using Zexoro.Formats.Linux;
using Zexoro.Platforms;
using Zexoro.Signing;
using Zexoro.Updates;

namespace Zexoro.Cli.Packaging;

/// <summary>Linux <c>apk</c>: an Alpine package, signed with <c>[linux.apk.signing]</c>, and with <c>[updates]</c> its release fragment.</summary>
internal sealed class LinuxApkFormatPackager : IPackager
{
    public PackageFormat Format => PackageFormat.Apk;

    public void Pack(PackContext context)
    {
        var config = context.Config;
        ApkSigningKey? key = null;
        try
        {
            ApkSigner? signer = null;
            if (config.Linux.Apk.Signing is { } signing)
            {
                try
                {
                    key = ApkSigningKey.Load(signing.ResolveKey(context.Environment.GetVariable));
                }
                catch (SigningException ex)
                {
                    throw new PackException($"{signing.Key.Key}: {ex.Message}", ex);
                }

                var signingKey = key;
                signer = new ApkSigner(signing.KeyName, signingKey.Description, control => signingKey.Sign(control));
            }

            var result = ApkPackager.Pack(new ApkPackRequest
            {
                Config = config,
                Architecture = context.Target.Architecture,
                OutputDirectory = context.OutputDirectory,
                Log = context.Log,
                ResolveUpdater = runtime => LinuxTarFormatPackager.ResolveUpdater(runtime, context.Environment),
                Signer = signer,
            });
            context.Log.WriteLine($"Created {result.PackagePath}");
            if (config.Updates is { } updates)
            {
                ReleaseFragments.Write(
                    context,
                    updates,
                    result.PackagePath,
                    new UpdateTarget(OperatingSystemFamily.Linux.Name, result.Architecture.Name, ApkPackager.FormatName, ApkPackager.Scope),
                    ApkPackager.MinimumUpdater);
            }
        }
        catch (LinuxPackException ex)
        {
            throw new PackException(ex.Message, ex);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new PackException($"Cannot sign the apk: {ex.Message}", ex);
        }
        finally
        {
            key?.Dispose();
        }
    }
}
