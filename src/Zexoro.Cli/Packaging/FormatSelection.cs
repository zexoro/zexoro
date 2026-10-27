using Zexoro.Configuration;
using Zexoro.Platforms;

namespace Zexoro.Cli.Packaging;

/// <summary>Chooses the formats of one <c>pack</c> run: <c>--format</c>, else the target platform's table, else its default.</summary>
internal static class FormatSelection
{
    /// <summary>
    /// Returns the formats to build for <paramref name="target"/>, or null after adding to
    /// <paramref name="errors"/> when a format is unknown, belongs to another operating system, is
    /// listed twice or is not implemented yet. Nothing is packed unless every format can be.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="target">The target platform.</param>
    /// <param name="option">The <c>--format</c> value (comma-separated), or null to use the configuration.</param>
    /// <param name="errors">Receives one message per problem.</param>
    public static IReadOnlyList<PackageFormat>? Select(ZexoroConfig config, PackTarget target, string? option, List<string> errors)
    {
        var os = target.Platform.OperatingSystem;
        var platform = config.Platform(os);
        if (platform is null)
        {
            errors.Add($"zexoro does not package for {os.Name}");
            return null;
        }

        var count = errors.Count;
        IReadOnlyList<PackageFormat> formats;
        if (option is null)
        {
            formats = platform.Formats;
        }
        else
        {
            var list = new List<PackageFormat>();
            var names = PackageFormat.NamesFor(os);
            foreach (var part in option.Split(','))
            {
                var name = part.Trim();
                if (name.Length == 0)
                {
                    errors.Add($"--format: '{option}' contains an empty format name");
                }
                else if (PackageFormat.TryFind(os, name, out var format))
                {
                    if (list.Contains(format))
                    {
                        errors.Add($"--format: '{name}' is listed twice");
                    }
                    else
                    {
                        list.Add(format);
                    }
                }
                else if (PackageFormat.OperatingSystemsWith(name) is { Count: > 0 } others)
                {
                    errors.Add($"--format: '{name}' is a {string.Join(" and ", others.Select(o => o.Name))} format, but the target is {target.Platform} ({target.Origin}); "
                        + $"formats for {os.Name}: {string.Join(", ", names)}");
                }
                else
                {
                    var suggestion = Suggestions.Closest(name, names) is { } match ? $"; did you mean '{match}'?" : string.Empty;
                    errors.Add($"--format: unknown format '{name}'; formats for {os.Name}: {string.Join(", ", names)}{suggestion}");
                }
            }

            formats = list;
        }

        if (errors.Count > count)
        {
            return null;
        }

        var implemented = Packagers.All.Where(p => p.Format.OperatingSystem == os).Select(p => p.Format.Name).ToList();
        foreach (var format in formats)
        {
            if (Packagers.Find(format) is null)
            {
                errors.Add($"the {os.Name} format '{format.Name}' is not implemented yet; this zexoro version packs "
                    + (implemented.Count == 0 ? $"no {os.Name} formats" : $"{string.Join(", ", implemented)} for {os.Name}"));
            }
        }

        return errors.Count > count ? null : formats;
    }
}
