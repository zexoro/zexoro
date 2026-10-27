using Zexoro.Cli.Toolsets;
using Zexoro.Formats.Zip;
using Zexoro.Platforms;

namespace Zexoro.Cli.Packaging;

/// <summary>Windows <c>zip</c>: a portable archive and, with <c>[updates]</c>, its release fragment.</summary>
internal sealed class WindowsZipFormatPackager : IPackager
{
    public PackageFormat Format => PackageFormat.WindowsZip;

    public void Pack(PackContext context)
    {
        var config = context.Config;
        var signing = WindowsPackaging.LoadSigning(context, "the application in the zip archive is not signed.");
        try
        {
            var result = WindowsZipPackager.Pack(new WindowsZipPackRequest
            {
                Config = config,
                Architecture = context.Target.Architecture,
                OutputDirectory = context.OutputDirectory,
                Signing = signing,
                Log = context.Log,
                ResolveUpdater = architecture => BundledUpdaters.Resolve(architecture, context.Environment),
            });
            context.Log.WriteLine($"Created {result.ArchivePath}");
            if (config.Updates is { } updates)
            {
                WindowsPackaging.WriteFragment(
                    context, updates, result.ArchivePath, result.Architecture, WindowsZipPackager.FormatName, WindowsZipPackager.Scope, WindowsZipPackager.MinimumUpdater);
            }
        }
        catch (ZipPackException ex)
        {
            throw new PackException(ex.Message, ex);
        }
        finally
        {
            signing?.Credential.Dispose();
        }
    }
}
