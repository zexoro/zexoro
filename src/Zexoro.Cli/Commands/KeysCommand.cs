using System.Text;
using Zexoro.Updates;

namespace Zexoro.Cli.Commands;

/// <summary>
/// <c>zexoro keys new [--output &lt;dir&gt;] [--name &lt;name&gt;]</c>: creates an update signing key pair
/// (docs/design/0005-nsis-updates.md section 2).
/// </summary>
internal static class KeysCommand
{
    public const string DefaultName = "zexoro-update";

    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CliEnvironment environment)
    {
        if (args.Count == 0 || args[0] != "new")
        {
            if (args.Count > 0 && args[0] is "-h" or "--help")
            {
                stdout.WriteLine(App.Usage);
                return ExitCodes.Success;
            }

            return App.UsageError(stderr, "keys: expected 'zexoro keys new'");
        }

        var options = CommandOptions.Parse("keys new", [.. args.Skip(1)], ["--output", "--name"], stdout, stderr, out var exitCode);
        if (options is null)
        {
            return exitCode;
        }

        var name = options.GetValueOrDefault("--name", DefaultName);
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') || name[0] == '.')
        {
            return App.UsageError(stderr, $"keys new: --name '{name}' may contain only ASCII letters, digits, '.', '-' and '_'");
        }

        var directory = Path.GetFullPath(options.GetValueOrDefault("--output", "."), environment.WorkingDirectory);
        var privatePath = Path.Combine(directory, name + ".key.pem");
        var publicPath = Path.Combine(directory, name + ".pub");
        foreach (var path in new[] { privatePath, publicPath })
        {
            if (File.Exists(path))
            {
                stderr.WriteLine($"zexoro: {path} already exists; keys new never overwrites a key");
                return ExitCodes.Failure;
            }
        }

        var password = environment.GetVariable(FeedCommand.KeyPasswordVariable);
        using var key = UpdateSigningKey.Create();
        try
        {
            Directory.CreateDirectory(directory);
            WriteNew(privatePath, key.ToPem(password), secret: true);
            WriteNew(publicPath, key.PublicKey.Text + "\n", secret: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"zexoro: cannot write the key pair: {ex.Message}");
            return ExitCodes.Failure;
        }

        stdout.WriteLine($"Private key: {privatePath}{(string.IsNullOrEmpty(password) ? string.Empty : " (encrypted)")}");
        stdout.WriteLine($"Public key:  {publicPath}");
        stdout.WriteLine();
        stdout.WriteLine(key.PublicKey.Text);
        stdout.WriteLine();
        stdout.WriteLine("Add the public key to [updates] keys in zexoro.toml. Keep the private key out of the");
        stdout.WriteLine("repository and back it up: if every trusted private key is lost, installed copies can");
        stdout.WriteLine("no longer be updated automatically.");
        return ExitCodes.Success;
    }

    private static void WriteNew(string path, string text, bool secret)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (secret && !OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        stream.Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text.ReplaceLineEndings("\n")));
    }
}
