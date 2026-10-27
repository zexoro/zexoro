using Zexoro.Cli.Toolsets;
using Zexoro.Formats.Nsis;
using Zexoro.Platforms;
using Zexoro.Toolsets;

namespace Zexoro.Cli.Packaging;

/// <summary>Windows <c>nsis</c>: an NSIS installer and, with <c>[updates]</c>, its release fragment.</summary>
internal sealed class NsisFormatPackager : IPackager
{
    public PackageFormat Format => PackageFormat.Nsis;

    public void Pack(PackContext context)
    {
        var config = context.Config;
        var stdout = context.Log;
        var signing = WindowsPackaging.LoadSigning(context, "the installer and the application are not signed.");
        try
        {
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
                var spec = result.Spec;
                WindowsPackaging.WriteFragment(context, updates, result.InstallerPath, spec.Architecture, "nsis", spec.ScopeName, NsisScript.MinimumUpdater);
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
}
