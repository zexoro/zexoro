using System.ComponentModel;
using System.Text.Json;

namespace Zexoro.Formats.MacOS;

/// <summary>A code-signing identity from <c>[macos.signing]</c>, resolved.</summary>
/// <param name="Identity">What <c>codesign --sign</c> takes: a certificate's common name or SHA-1 hash.</param>
/// <param name="Keychain">The keychain to look the identity up in, or null for the search list.</param>
/// <param name="Entitlements">The entitlements file applied to the application, or null.</param>
public sealed record MacSigningIdentity(string Identity, string? Keychain, string? Entitlements);

/// <summary>An App Store Connect API key from <c>[macos.notarization]</c>, resolved.</summary>
/// <param name="KeyPath">The <c>.p8</c> file.</param>
/// <param name="KeyId">The key ID.</param>
/// <param name="Issuer">The issuer ID.</param>
public sealed record MacNotarizationKey(string KeyPath, string KeyId, string Issuer);

/// <summary>
/// Signs a bundle with <c>codesign</c>, inside out (docs/design/0006-desktop-formats.md section 3):
/// every nested code item (Mach-O files and nested bundles such as frameworks, deepest first, which
/// includes <c>zexoro-updater</c>), then the bundle itself, whose signature covers its main
/// executable, Info.plist and every resource. With an identity each item is signed with
/// <c>--options runtime --timestamp</c> (hardened runtime and a secure timestamp, which
/// notarization requires) and the entitlements go on the bundle only; without one, ad hoc
/// (<c>--sign -</c>) without hardened runtime, so a framework-dependent application can still load
/// a runtime signed by someone else.
/// </summary>
public static class MacCodeSigner
{
    public const string Codesign = "/usr/bin/codesign";
    public const string Xcrun = "/usr/bin/xcrun";

    /// <summary>Directory extensions of nested bundles that are code (signed as a unit).</summary>
    internal static readonly string[] BundleExtensions = [".app", ".framework", ".appex", ".xpc", ".bundle", ".plugin", ".kext", ".systemextension"];

    /// <summary>How many times a timestamp failure is retried, and how long to wait between attempts.</summary>
    internal static int TimestampAttempts { get; set; } = 4;

    internal static TimeSpan TimestampRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Returns the nested code items of <paramref name="bundle"/> relative to it with '/'
    /// separators, deepest first: regular Mach-O files and nested bundle directories, but not the
    /// bundle's own main executable. Symbolic links are not followed.
    /// </summary>
    /// <param name="bundle">The bundle directory.</param>
    /// <param name="mainExecutable">The main executable relative to the bundle (<c>Contents/MacOS/&lt;name&gt;</c>).</param>
    /// <exception cref="IOException">The bundle cannot be read.</exception>
    public static List<string> FindNestedCode(string bundle, string mainExecutable)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundle);
        ArgumentNullException.ThrowIfNull(mainExecutable);
        var items = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };

        void Visit(string directory, string relative)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", options))
            {
                var name = relative.Length == 0 ? Path.GetFileName(path) : relative + "/" + Path.GetFileName(path);
                if (new FileInfo(path).LinkTarget is not null)
                {
                    continue;
                }

                if (Directory.Exists(path))
                {
                    if (BundleExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                    {
                        items.Add(name);
                    }

                    Visit(path, name);
                }
                else if (name != mainExecutable && FatMachO.IsMachOFile(path))
                {
                    items.Add(name);
                }
            }
        }

        Visit(Path.GetFullPath(bundle), string.Empty);
        items.Sort((a, b) =>
        {
            var depth = b.Count(c => c == '/').CompareTo(a.Count(c => c == '/'));
            return depth != 0 ? depth : string.CompareOrdinal(a, b);
        });
        return items;
    }

    /// <summary>The <c>codesign</c> arguments for one item; entitlements only for the bundle itself.</summary>
    internal static List<string> SignArguments(MacSigningIdentity? identity, string path, bool isBundle)
    {
        if (identity is null)
        {
            return ["--force", "--sign", "-", path];
        }

        var arguments = new List<string> { "--force", "--sign", identity.Identity, "--options", "runtime", "--timestamp" };
        if (identity.Keychain is { } keychain)
        {
            arguments.Add("--keychain");
            arguments.Add(keychain);
        }

        if (isBundle && identity.Entitlements is { } entitlements)
        {
            arguments.Add("--entitlements");
            arguments.Add(entitlements);
        }

        arguments.Add(path);
        return arguments;
    }

    /// <summary>Signs every nested code item, then the bundle; then verifies it as <c>codesign --verify --deep --strict</c> does.</summary>
    /// <exception cref="MacPackException">codesign is missing or fails.</exception>
    public static void Sign(IMacToolRunner tools, string bundle, string mainExecutable, MacSigningIdentity? identity, TextWriter? log)
    {
        ArgumentNullException.ThrowIfNull(tools);
        List<string> nested;
        try
        {
            nested = FindNestedCode(bundle, mainExecutable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MacPackException($"Cannot read the bundle to sign it: {ex.Message}", ex);
        }

        log?.WriteLine(identity is null
            ? $"Signing {Path.GetFileName(bundle)} ad hoc ({nested.Count} nested code item(s))"
            : $"Signing {Path.GetFileName(bundle)} as '{identity.Identity}' ({nested.Count} nested code item(s))");
        foreach (var item in nested)
        {
            RunCodesign(tools, SignArguments(identity, Path.Combine(bundle, item), isBundle: false), item);
        }

        RunCodesign(tools, SignArguments(identity, bundle, isBundle: true), Path.GetFileName(bundle));
        Verify(tools, bundle);
    }

    /// <summary>Runs <c>codesign --verify --deep --strict</c> on the bundle.</summary>
    /// <exception cref="MacPackException">The signature is not valid.</exception>
    public static void Verify(IMacToolRunner tools, string bundle)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var result = Run(tools, Codesign, ["--verify", "--deep", "--strict", "--verbose=2", bundle]);
        if (result.ExitCode != 0)
        {
            throw new MacPackException($"codesign --verify rejects the signed bundle: {Describe(result)}");
        }
    }

    private static void RunCodesign(IMacToolRunner tools, List<string> arguments, string what)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = Run(tools, Codesign, arguments);
            if (result.ExitCode == 0)
            {
                return;
            }

            // Apple's timestamp service fails now and then ("The timestamp service is not available.").
            if (attempt < TimestampAttempts && result.Error.Contains("timestamp service", StringComparison.OrdinalIgnoreCase))
            {
                Thread.Sleep(TimestampRetryDelay);
                continue;
            }

            throw new MacPackException($"codesign cannot sign {what}: {Describe(result)}");
        }
    }

    /// <summary>Runs a tool, turning a missing tool into a <see cref="MacPackException"/>.</summary>
    internal static ToolResult Run(IMacToolRunner tools, string fileName, IReadOnlyList<string> arguments)
    {
        try
        {
            return tools.Run(fileName, arguments);
        }
        catch (Win32Exception ex)
        {
            throw new MacPackException(
                $"Cannot run {fileName}: {ex.Message}. macOS packages need the system's code-signing tools"
                + (fileName == Xcrun ? " and the Xcode command line tools (xcode-select --install)" : string.Empty) + ".",
                ex);
        }
    }

    internal static string Describe(ToolResult result)
    {
        var text = (result.Error.Trim() + "\n" + result.Output.Trim()).Trim();
        return text.Length == 0 ? $"exit code {result.ExitCode}" : $"{text} (exit code {result.ExitCode})";
    }
}

/// <summary>
/// Notarizes a signed bundle (docs/design/0006-desktop-formats.md section 3): submits a zip of it
/// with <c>xcrun notarytool submit --wait</c> and an App Store Connect API key, fetches the log
/// when Apple does not accept it, and staples the ticket to the bundle with <c>xcrun stapler
/// staple</c>. A zip cannot be stapled, so the caller zips the bundle again afterwards.
/// </summary>
public static class MacNotarizer
{
    /// <summary>Submits <paramref name="zip"/> and staples <paramref name="bundle"/>.</summary>
    /// <exception cref="MacPackException">Submission fails, Apple rejects the bundle, or stapling fails.</exception>
    public static void Notarize(IMacToolRunner tools, string zip, string bundle, MacNotarizationKey key, TextWriter? log)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(key);
        log?.WriteLine($"Notarizing {Path.GetFileName(bundle)} (xcrun notarytool submit --wait; this takes minutes)");
        var result = MacCodeSigner.Run(tools, MacCodeSigner.Xcrun, ["notarytool", "submit", zip, .. KeyArguments(key), "--wait", "--output-format", "json"]);
        var (id, status, message) = ParseSubmission(result.Output);
        if (result.ExitCode != 0 || !string.Equals(status, "Accepted", StringComparison.Ordinal))
        {
            var details = MacCodeSigner.Describe(result);
            if (id is not null)
            {
                var logResult = MacCodeSigner.Run(tools, MacCodeSigner.Xcrun, ["notarytool", "log", id, .. KeyArguments(key)]);
                if (logResult.ExitCode == 0)
                {
                    details += "\n" + logResult.Output.Trim();
                }
            }

            throw new MacPackException($"Notarization failed (status {status ?? "unknown"}{(message is null ? string.Empty : $": {message}")}): {details}");
        }

        log?.WriteLine($"Notarized (submission {id}); stapling the ticket");
        var staple = MacCodeSigner.Run(tools, MacCodeSigner.Xcrun, ["stapler", "staple", bundle]);
        if (staple.ExitCode != 0)
        {
            throw new MacPackException($"xcrun stapler cannot staple the notarization ticket: {MacCodeSigner.Describe(staple)}");
        }
    }

    internal static string[] KeyArguments(MacNotarizationKey key) => ["--key", key.KeyPath, "--key-id", key.KeyId, "--issuer", key.Issuer];

    /// <summary>Reads <c>id</c>, <c>status</c> and <c>message</c> from notarytool's JSON output; nulls when it is not JSON.</summary>
    internal static (string? Id, string? Status, string? Message) ParseSubmission(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null, null);
            }

            return (Text(root, "id"), Text(root, "status"), Text(root, "message"));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }

        static string? Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
