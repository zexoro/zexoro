using Zexoro.Cli.Toolsets;
using Zexoro.Formats.Linux;
using Zexoro.Platforms;
using Zexoro.Updates;

namespace Zexoro.Cli.Packaging;

/// <summary>Linux <c>tar.gz</c>: a portable archive and, with <c>[updates]</c>, its release fragment.</summary>
internal sealed class LinuxTarFormatPackager : IPackager
{
    public PackageFormat Format => PackageFormat.TarGz;

    public void Pack(PackContext context)
    {
        var config = context.Config;
        try
        {
            var result = LinuxTarPackager.Pack(new LinuxTarPackRequest
            {
                Config = config,
                Architecture = context.Target.Architecture,
                OutputDirectory = context.OutputDirectory,
                Log = context.Log,
                ResolveUpdater = runtime => ResolveUpdater(runtime, context.Environment),
            });
            context.Log.WriteLine($"Created {result.ArchivePath}");
            if (config.Updates is { } updates)
            {
                ReleaseFragments.Write(
                    context,
                    updates,
                    result.ArchivePath,
                    new UpdateTarget(OperatingSystemFamily.Linux.Name, result.Architecture.Name, LinuxTarPackager.FormatName, LinuxTarPackager.Scope),
                    LinuxTarPackager.MinimumUpdater);
            }
        }
        catch (LinuxPackException ex)
        {
            throw new PackException(ex.Message, ex);
        }
    }

    internal static UnixUpdaterBinary ResolveUpdater(UpdaterRuntime runtime, CliEnvironment environment)
    {
        try
        {
            var updater = BundledUpdaters.Resolve(runtime, environment);
            return new UnixUpdaterBinary(updater.Runtime, updater.Source, updater.Sha256, updater.Open);
        }
        catch (Exception ex) when (ex is UpdaterUnavailableException or IOException or UnauthorizedAccessException)
        {
            throw new LinuxPackException(ex.Message, ex);
        }
    }
}
