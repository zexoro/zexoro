using Zexoro.Formats.Linux;
using Zexoro.Platforms;
using Zexoro.Updates;

namespace Zexoro.Cli.Packaging;

/// <summary>Linux <c>deb</c>: a Debian package and, with <c>[updates]</c>, its release fragment.</summary>
internal sealed class LinuxDebFormatPackager : IPackager
{
    public PackageFormat Format => PackageFormat.Deb;

    public void Pack(PackContext context)
    {
        var config = context.Config;
        try
        {
            var result = DebPackager.Pack(new DebPackRequest
            {
                Config = config,
                Architecture = context.Target.Architecture,
                OutputDirectory = context.OutputDirectory,
                Log = context.Log,
                ResolveUpdater = runtime => LinuxTarFormatPackager.ResolveUpdater(runtime, context.Environment),
            });
            context.Log.WriteLine($"Created {result.PackagePath}");
            if (config.Updates is { } updates)
            {
                ReleaseFragments.Write(
                    context,
                    updates,
                    result.PackagePath,
                    new UpdateTarget(OperatingSystemFamily.Linux.Name, result.Architecture.Name, DebPackager.FormatName, DebPackager.Scope),
                    DebPackager.MinimumUpdater);
            }
        }
        catch (LinuxPackException ex)
        {
            throw new PackException(ex.Message, ex);
        }
    }
}
