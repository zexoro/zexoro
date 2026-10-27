using System.Text;
using Zexoro.Updates;

namespace Zexoro.Cli.Commands;

/// <summary>
/// <c>zexoro feed add</c> merges release fragments into a feed and signs it; <c>zexoro feed verify</c>
/// checks a feed against a public key (docs/design/0005-nsis-updates.md section 2).
/// </summary>
internal static class FeedCommand
{
    /// <summary>The PEM text of the private key, for CI secrets; <c>--key</c> takes precedence.</summary>
    public const string KeyVariable = "ZEXORO_UPDATE_KEY";

    /// <summary>The password of an encrypted private key (also used by <c>keys new</c> to encrypt).</summary>
    public const string KeyPasswordVariable = "ZEXORO_UPDATE_KEY_PASSWORD";

    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CliEnvironment environment)
    {
        if (args.Count > 0 && args[0] is "-h" or "--help")
        {
            stdout.WriteLine(App.Usage);
            return ExitCodes.Success;
        }

        return args.Count > 0 && args[0] == "add" ? Add([.. args.Skip(1)], stdout, stderr, environment)
            : args.Count > 0 && args[0] == "verify" ? Verify([.. args.Skip(1)], stdout, stderr, environment)
            : App.UsageError(stderr, "feed: expected 'zexoro feed add' or 'zexoro feed verify'");
    }

    private static int Add(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CliEnvironment environment)
    {
        if (!TryParse("feed add", args, ["--feed", "--key"], stdout, stderr, out var options, out var files, out var exitCode))
        {
            return exitCode;
        }

        if (!options.TryGetValue("--feed", out var feedOption) || files.Count == 0)
        {
            return App.UsageError(stderr, "feed add: usage: zexoro feed add --feed <feed.json> [--key <file>] <fragment.json>...");
        }

        var feedPath = Path.GetFullPath(feedOption, environment.WorkingDirectory);
        var signaturePath = feedPath + ".sig";
        string pem;
        if (options.TryGetValue("--key", out var keyOption))
        {
            var keyPath = Path.GetFullPath(keyOption, environment.WorkingDirectory);
            if (!TryRead(keyPath, stderr, out var keyBytes))
            {
                return ExitCodes.Failure;
            }

            pem = Encoding.UTF8.GetString(keyBytes);
        }
        else if (environment.GetVariable(KeyVariable) is { Length: > 0 } fromEnvironment)
        {
            pem = fromEnvironment;
        }
        else
        {
            stderr.WriteLine($"zexoro: feed add needs the private key: pass --key <file> or set {KeyVariable} to the PEM text");
            return ExitCodes.Failure;
        }

        try
        {
            using var key = UpdateSigningKey.FromPem(pem, environment.GetVariable(KeyPasswordVariable));
            UpdateFeed? original = null;
            if (File.Exists(feedPath))
            {
                if (!TryRead(feedPath, stderr, out var existing))
                {
                    return ExitCodes.Failure;
                }

                original = UpdateFeed.Parse(existing);
            }

            var published = environment.Time.GetUtcNow();
            var feed = original;
            foreach (var file in files)
            {
                var fragmentPath = Path.GetFullPath(file, environment.WorkingDirectory);
                if (!TryRead(fragmentPath, stderr, out var bytes))
                {
                    return ExitCodes.Failure;
                }

                ReleaseFragment fragment;
                try
                {
                    fragment = ReleaseFragment.Parse(bytes);
                }
                catch (UpdateException ex)
                {
                    stderr.WriteLine($"zexoro: {fragmentPath}: {ex.Message}");
                    return ExitCodes.Failure;
                }

                feed = fragment.AddTo(feed, published);
            }

            if (feed is null || ReferenceEquals(feed, original))
            {
                stdout.WriteLine($"{feedPath} already contains these releases; nothing changed.");
                return ExitCodes.Success;
            }

            // One publish is one sequence step, however many fragments it merges.
            feed = new UpdateFeed(feed.App, feed.Channel, (original?.Sequence ?? 0) + 1, published, feed.Releases);
            var json = feed.ToJson();
            WriteReplacing(feedPath, json);
            WriteReplacing(signaturePath, FeedSignature.Sign(json, key));
            stdout.WriteLine($"Updated {feedPath} (app {feed.App}, channel {feed.Channel}, sequence {feed.Sequence}).");
            stdout.WriteLine($"Signed {signaturePath} with {key.PublicKey.Text}");
            stdout.WriteLine("Upload the artifacts first, then feed.json and feed.json.sig.");
            return ExitCodes.Success;
        }
        catch (UpdateException ex)
        {
            stderr.WriteLine($"zexoro: {ex.Message}");
            return ExitCodes.Failure;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"zexoro: cannot write the feed: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    private static int Verify(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CliEnvironment environment)
    {
        if (!TryParse("feed verify", args, ["--public-key"], stdout, stderr, out var options, out var files, out var exitCode))
        {
            return exitCode;
        }

        if (!options.TryGetValue("--public-key", out var keyOption) || files.Count != 1)
        {
            return App.UsageError(stderr, "feed verify: usage: zexoro feed verify --public-key <key or .pub file> <feed.json>");
        }

        var keyText = keyOption;
        var keyFile = Path.GetFullPath(keyOption, environment.WorkingDirectory);
        if (File.Exists(keyFile))
        {
            if (!TryRead(keyFile, stderr, out var keyBytes))
            {
                return ExitCodes.Failure;
            }

            keyText = Encoding.UTF8.GetString(keyBytes);
        }

        var feedPath = Path.GetFullPath(files[0], environment.WorkingDirectory);
        if (!TryRead(feedPath, stderr, out var feedBytes) || !TryRead(feedPath + ".sig", stderr, out var signature))
        {
            return ExitCodes.Failure;
        }

        UpdateFeed feed;
        try
        {
            feed = FeedVerifier.Verify(feedBytes, signature, [UpdatePublicKey.Parse(keyText)]);
        }
        catch (UpdateException ex)
        {
            stderr.WriteLine($"zexoro: {feedPath}: {ex.Message}");
            return ExitCodes.Failure;
        }

        stdout.WriteLine($"{feedPath}: signature valid");
        stdout.WriteLine($"app {feed.App}, channel {feed.Channel}, sequence {feed.Sequence}, published {feed.Published:yyyy-MM-dd'T'HH:mm:ss'Z'}");
        foreach (var release in feed.Releases)
        {
            stdout.WriteLine($"  {release.Version}");
            foreach (var artifact in release.Artifacts)
            {
                stdout.WriteLine($"    {artifact.Target}  {artifact.Size} bytes  {artifact.Url}");
            }
        }

        return ExitCodes.Success;
    }

    /// <summary>Parses <c>--name value</c> options (each at most once) and positional arguments.</summary>
    private static bool TryParse(
        string command,
        IReadOnlyList<string> args,
        string[] names,
        TextWriter stdout,
        TextWriter stderr,
        out Dictionary<string, string> options,
        out List<string> positional,
        out int exitCode)
    {
        options = new Dictionary<string, string>(StringComparer.Ordinal);
        positional = [];
        exitCode = ExitCodes.Success;
        var optionArgs = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith('-'))
            {
                optionArgs.Add(arg);
                if (!arg.Contains('=', StringComparison.Ordinal) && arg is not ("-h" or "--help") && i + 1 < args.Count)
                {
                    optionArgs.Add(args[++i]);
                }
            }
            else
            {
                positional.Add(arg);
            }
        }

        var parsed = CommandOptions.Parse(command, optionArgs, names, stdout, stderr, out exitCode);
        if (parsed is null)
        {
            return false;
        }

        options = parsed;
        return true;
    }

    private static bool TryRead(string path, TextWriter stderr, out byte[] bytes)
    {
        try
        {
            bytes = File.ReadAllBytes(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"zexoro: cannot read {path}: {ex.Message}");
            bytes = [];
            return false;
        }
    }

    /// <summary>Writes a temporary sibling, then renames it over <paramref name="path"/>.</summary>
    private static void WriteReplacing(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
