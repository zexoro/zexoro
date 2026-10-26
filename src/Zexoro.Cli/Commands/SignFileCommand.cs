using Zexoro.Formats.Nsis;
using Zexoro.Signing;

namespace Zexoro.Cli.Commands;

/// <summary>
/// <c>zexoro sign-file --request &lt;json&gt; &lt;file&gt;</c>: an internal command that makensis runs
/// (through <c>!uninstfinalize</c> and <c>!finalize</c>) to sign the uninstaller and the installer.
/// Not listed in the usage text. The password comes from the environment, never the command line.
/// </summary>
internal static class SignFileCommand
{
    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CliEnvironment environment)
    {
        string? requestPath = null;
        string? file = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--request" && i + 1 < args.Count && requestPath is null)
            {
                requestPath = args[++i];
            }
            else if (file is null && !args[i].StartsWith("--", StringComparison.Ordinal))
            {
                file = args[i];
            }
            else
            {
                return App.UsageError(stderr, $"sign-file: unrecognized argument: {args[i]}");
            }
        }

        if (requestPath is null || file is null)
        {
            return App.UsageError(stderr, "sign-file: usage: zexoro sign-file --request <json> <file>");
        }

        var target = Path.GetFullPath(file, environment.WorkingDirectory);
        try
        {
            var request = SignFileRequest.Read(Path.GetFullPath(requestPath, environment.WorkingDirectory));
            var password = environment.GetVariable(SignFileRequest.PasswordVariable);
            using var credential = SigningCredential.LoadPfx(request.Certificate, string.IsNullOrEmpty(password) ? null : password);
            AuthenticodeSigner.SignFile(target, new SigningOptions
            {
                Credential = credential,
                TimestampUrl = request.Timestamp,
                Description = request.Description,
                DescriptionUrl = request.DescriptionUrl,
            });
        }
        catch (Exception ex) when (ex is SigningException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"zexoro: cannot sign {target}: {ex.Message}");
            return ExitCodes.Failure;
        }

        stdout.WriteLine($"Signed {target}");
        return ExitCodes.Success;
    }
}
