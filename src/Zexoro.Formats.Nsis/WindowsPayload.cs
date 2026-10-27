using Zexoro.Configuration;
using Zexoro.Platforms;
using Zexoro.Signing;

namespace Zexoro.Formats.Nsis;

/// <summary>
/// What every Windows format with the update contract's layout (docs/design/0002-update-contract.md
/// section 7) stages the same way: the artifact name, the copied and signed application directory,
/// and the signed updater. Shared by the NSIS installer and the portable zip.
/// </summary>
public static class WindowsPayload
{
    /// <summary>
    /// The name part of artifact file names: <c>app.name</c> with everything but letters, digits,
    /// '.', '-' and '_' removed (the last segment of <c>app.id</c> if nothing is left), so file names
    /// need no quoting in shells or URLs.
    /// </summary>
    public static string ArtifactName(AppConfig app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var name = string.Concat(app.Name.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_')).Trim('.');
        return name.Length > 0 ? name : app.Id[(app.Id.LastIndexOf('.') + 1)..];
    }

    /// <summary>The artifact file name <c>&lt;name&gt;-&lt;version&gt;-win-&lt;arch&gt;&lt;extension&gt;</c>.</summary>
    public static string ArtifactFileName(AppConfig app, Architecture architecture, string extension)
    {
        ArgumentNullException.ThrowIfNull(app);
        return $"{ArtifactName(app)}-{app.Version}-win-{architecture.Name}{extension}";
    }

    /// <summary>
    /// Copies the input directory, including hidden files, skipping <paramref name="exclude"/> (the
    /// output directory, when it lies inside the input). Returns the empty directories (relative,
    /// forward slashes).
    /// </summary>
    public static List<string> CopyDirectory(string source, string destination, string exclude)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };
        var empty = new List<string>();
        var excludePrefix = exclude.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        void Copy(string from, string to, string relative)
        {
            Directory.CreateDirectory(to);
            var any = false;
            foreach (var file in Directory.EnumerateFiles(from, "*", options).Order(StringComparer.Ordinal))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
                any = true;
            }

            foreach (var directory in Directory.EnumerateDirectories(from, "*", options).Order(StringComparer.Ordinal))
            {
                if ((directory + Path.DirectorySeparatorChar).StartsWith(excludePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                any = true;
                var name = Path.GetFileName(directory);
                Copy(directory, Path.Combine(to, name), relative.Length == 0 ? name : relative + "/" + name);
            }

            if (!any && relative.Length > 0)
            {
                empty.Add(relative);
            }
        }

        Copy(source, destination, string.Empty);
        return empty;
    }

    /// <summary>
    /// Signs the staged application files (<c>[windows] sign</c>): the main executable, or every
    /// exe and dll. Files that are already signed keep their signature. Returns the signed files,
    /// relative to <paramref name="appDirectory"/>.
    /// </summary>
    /// <exception cref="SigningException">Signing failed.</exception>
    public static List<string> SignApplicationFiles(
        string appDirectory, string main, WindowsSignFiles which, NsisSigning signing, AppConfig app, TextWriter? log)
    {
        ArgumentNullException.ThrowIfNull(signing);
        ArgumentNullException.ThrowIfNull(app);
        IEnumerable<string> candidates = which == WindowsSignFiles.All
            ? Directory.EnumerateFiles(appDirectory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
            : [Path.Combine(appDirectory, main.Replace('/', Path.DirectorySeparatorChar))];

        var options = Options(signing, app);
        var signed = new List<string>();
        foreach (var file in candidates)
        {
            var relative = Path.GetRelativePath(appDirectory, file).Replace('\\', '/');
            if (AuthenticodeSigner.IsSigned(file))
            {
                log?.WriteLine($"Skipping {relative}: already signed");
                continue;
            }

            log?.WriteLine($"Signing {relative}");
            AuthenticodeSigner.SignFile(file, options);
            signed.Add(relative);
        }

        return signed;
    }

    /// <summary>
    /// Writes the updater to <paramref name="path"/> (checking its hash) and, with signing, signs it
    /// with the application's certificate, so it carries the same publisher as the files it updates.
    /// An updater that is already signed keeps its signature, like application files.
    /// </summary>
    /// <exception cref="NsisException">The updater does not match its expected hash or architecture.</exception>
    /// <exception cref="SigningException">Signing failed.</exception>
    public static void StageUpdater(UpdaterBinary updater, string path, NsisSigning? signing, AppConfig app, TextWriter? log)
    {
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentNullException.ThrowIfNull(app);
        log?.WriteLine($"Adding {UpdaterBinary.FileName} ({updater.Source})");
        updater.WriteTo(path);
        if (signing is null)
        {
            return;
        }

        if (AuthenticodeSigner.IsSigned(path))
        {
            log?.WriteLine($"Skipping {UpdaterBinary.FileName}: already signed");
            return;
        }

        log?.WriteLine($"Signing {UpdaterBinary.FileName}");
        AuthenticodeSigner.SignFile(path, Options(signing, app));
    }

    private static SigningOptions Options(NsisSigning signing, AppConfig app) => new()
    {
        Credential = signing.Credential,
        TimestampUrl = signing.Timestamp,
        Description = app.Name,
        DescriptionUrl = app.Url,
    };
}
