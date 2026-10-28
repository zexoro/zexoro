using Zexoro.Configuration;
using Zexoro.Formats.MacOS;
using Zexoro.Platforms;
using Zexoro.Updates;

namespace Zexoro.Cli.Packaging;

/// <summary>
/// macOS <c>zip</c>: a signed <c>.app</c> bundle in a zip and, with <c>[updates]</c>, its release
/// fragment. Packs only on macOS hosts (docs/design/0006-desktop-formats.md section 3).
/// </summary>
internal sealed class MacZipFormatPackager : IPackager
{
    public PackageFormat Format => PackageFormat.MacOSZip;

    public void Pack(PackContext context)
    {
        var config = context.Config;
        var environment = context.Environment;
        if (!environment.IsMacOSHost)
        {
            throw new PackException(
                "macOS bundles pack only on macOS: signing needs the system's codesign (and notarization Xcode's notarytool), which exist nowhere else. "
                + "Run zexoro pack on a Mac or a macOS CI runner.");
        }

        try
        {
            var result = MacZipPackager.Pack(new MacZipPackRequest
            {
                Config = config,
                Architecture = context.Target.Architecture,
                OutputDirectory = context.OutputDirectory,
                Tools = environment.MacTools,
                Signing = ResolveSigning(config.MacOS.Signing, environment),
                Notarization = ResolveNotarization(config.MacOS.Notarization, environment),
                ResolveUpdater = runtime => LinuxTarFormatPackager.ResolveUpdater(runtime, environment),
                Log = context.Log,
            });
            context.Log.WriteLine($"Created {result.ZipPath}");
            if (config.Updates is { } updates)
            {
                ReleaseFragments.Write(
                    context,
                    updates,
                    result.ZipPath,
                    new UpdateTarget(OperatingSystemFamily.MacOS.Name, result.Architecture.Name, MacBundle.RecordFormat, MacBundle.Scope),
                    MacZipPackager.MinimumUpdater);
            }
        }
        catch (MacPackException ex)
        {
            throw new PackException(ex.Message, ex);
        }
        catch (Formats.Linux.LinuxPackException ex)
        {
            throw new PackException(ex.Message, ex);
        }
    }

    internal static MacSigningIdentity? ResolveSigning(MacOSSigningConfig? signing, CliEnvironment environment) =>
        signing is null
            ? null
            : new MacSigningIdentity(signing.Identity.Resolve(environment.GetVariable), signing.ResolveKeychain(environment.GetVariable), signing.Entitlements?.FullPath);

    internal static MacNotarizationKey? ResolveNotarization(MacOSNotarizationConfig? notarization, CliEnvironment environment) =>
        notarization is null
            ? null
            : new MacNotarizationKey(
                notarization.ResolveKeyPath(environment.GetVariable),
                notarization.KeyId.Resolve(environment.GetVariable),
                notarization.Issuer.Resolve(environment.GetVariable));
}
