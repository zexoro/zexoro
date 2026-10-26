using System.Text;
using Zexoro.Configuration;
using Zexoro.Platforms;
using Zexoro.Signing;

namespace Zexoro.Formats.Nsis;

/// <summary>Signing settings for <see cref="NsisPackager.Pack"/>.</summary>
public sealed record NsisSigning
{
    /// <summary>The loaded certificate, used to sign the application files in process.</summary>
    public required SigningCredential Credential { get; init; }

    /// <summary>The PFX file, which <c>sign-file</c> loads again to sign the uninstaller and the installer.</summary>
    public required string CertificatePath { get; init; }

    public string? Password { get; init; }

    public Uri? Timestamp { get; init; }

    /// <summary>
    /// The command line that runs zexoro itself (for example <c>[/usr/local/bin/zexoro]</c>, or
    /// <c>[dotnet, exec, zexoro.dll]</c>); makensis appends <c>sign-file --request &lt;file&gt; &lt;target&gt;</c>.
    /// </summary>
    public required IReadOnlyList<string> ZexoroCommand { get; init; }
}

/// <summary>What <see cref="NsisPackager.Pack"/> needs.</summary>
public sealed record NsisPackRequest
{
    public required ZexoroConfig Config { get; init; }

    public required NsisToolset Toolset { get; init; }

    /// <summary>The directory the installer is written to; created if missing.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Signing settings, or null to build unsigned.</summary>
    public NsisSigning? Signing { get; init; }

    /// <summary>Receives progress messages; null for none.</summary>
    public TextWriter? Log { get; init; }

    /// <summary>Keeps the staging directory (script, staged files) for inspection instead of deleting it.</summary>
    public bool KeepStaging { get; init; }
}

/// <summary>The result of <see cref="NsisPackager.Pack"/>.</summary>
/// <param name="InstallerPath">The installer that was written.</param>
/// <param name="Spec">What the installer was built from.</param>
/// <param name="SignedFiles">Application files signed before packaging, relative to the input directory.</param>
/// <param name="StagingDirectory">The staging directory, if it was kept; otherwise null.</param>
public sealed record NsisPackResult(string InstallerPath, NsisInstallerSpec Spec, IReadOnlyList<string> SignedFiles, string? StagingDirectory);

/// <summary>Builds a Windows installer with NSIS: stage, sign, generate the script, run makensis.</summary>
public static class NsisPackager
{
    /// <summary>The script file name in the staging directory.</summary>
    public const string ScriptFileName = "installer.nsi";

    private const string SignRequestFileName = "sign.json";
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// The installer file name: <c>&lt;name&gt;-&lt;version&gt;-win-&lt;arch&gt;.exe</c>, for example
    /// <c>ExampleNotes-1.2.3-win-x64.exe</c>. The name is <c>app.name</c> with everything but
    /// letters, digits, '.', '-' and '_' removed (the last segment of <c>app.id</c> if nothing is
    /// left), so the file name needs no quoting in shells or URLs. It deliberately avoids "setup":
    /// Windows applies installer compatibility shims to such names (makensis warns about it).
    /// </summary>
    public static string GetOutputFileName(AppConfig app, Architecture architecture)
    {
        ArgumentNullException.ThrowIfNull(app);
        var name = string.Concat(app.Name.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_')).Trim('.');
        if (name.Length == 0)
        {
            name = app.Id[(app.Id.LastIndexOf('.') + 1)..];
        }

        return $"{name}-{app.Version}-win-{architecture.Name}.exe";
    }

    /// <summary>Builds the installer.</summary>
    /// <exception cref="NsisException">An input is missing or invalid, signing fails, or makensis fails.</exception>
    public static NsisPackResult Pack(NsisPackRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = request.Config;
        var windows = config.Windows
            ?? throw new NsisException($"{config.Source}: there is no [windows] table, so there is nothing to build; add one to build a Windows installer.");
        var input = config.Input;
        if (!Directory.Exists(input.Directory.FullPath))
        {
            throw new NsisException($"input.dir: directory {input.Directory.FullPath} not found; build your application first.");
        }

        if (!File.Exists(input.Main.FullPath))
        {
            throw new NsisException($"input.main: {input.Main.FullPath} not found.");
        }

        var architecture = PeMachine.Read(input.Main.FullPath)
            ?? throw new NsisException($"input.main: {input.Main.FullPath} is not a Windows executable for x86, x64 or arm64.");
        if (!File.Exists(config.App.Icon.FullPath))
        {
            throw new NsisException($"app.icon: {config.App.Icon.FullPath} not found.");
        }

        if (!IsIco(config.App.Icon.FullPath))
        {
            throw new NsisException($"app.icon: {config.App.Icon.FullPath} is not an .ico file, which Windows installers need.");
        }

        if (windows.License is { } licensePath && !File.Exists(licensePath.FullPath))
        {
            throw new NsisException($"windows.license: {licensePath.FullPath} not found.");
        }

        var outputDirectory = Path.GetFullPath(request.OutputDirectory);
        var staging = Directory.CreateTempSubdirectory("zexoro-nsis-").FullName;
        var keep = request.KeepStaging;
        try
        {
            var appDirectory = Path.Combine(staging, NsisInstallerSpec.AppDirectoryName);
            var (size, emptyDirectories) = CopyDirectory(input.Directory.FullPath, appDirectory, outputDirectory);
            var signed = request.Signing is { } signing
                ? SignApplicationFiles(appDirectory, input.Main.Value, windows.Sign, signing, config.App, request.Log)
                : [];

            File.Copy(config.App.Icon.FullPath, Path.Combine(staging, NsisInstallerSpec.IconFileName));
            string? license = null;
            if (windows.License is { } l)
            {
                license = StageLicense(l.FullPath, staging);
            }

            var spec = new NsisInstallerSpec
            {
                AppId = config.App.Id,
                Name = config.App.Name,
                Version = config.App.Version,
                Publisher = config.App.Publisher,
                Description = config.App.Description,
                Url = config.App.Url,
                MainExecutable = input.Main.Value,
                Architecture = architecture,
                Scope = windows.InstallScope,
                Shortcuts = windows.Shortcuts,
                LicenseFileName = license,
                UninstallRemove = windows.Uninstall.Remove,
                EstimatedSizeKb = (size + 1023) / 1024,
                EmptyDirectories = emptyDirectories,
                SignCommand = request.Signing is { } s ? WriteSignCommand(staging, s, config.App) : null,
            };

            var record = InstallRecord.Create(spec);
            File.WriteAllBytes(Path.Combine(staging, NsisInstallerSpec.InstallRecordFileName), record);
            File.WriteAllText(Path.Combine(staging, ScriptFileName), NsisScript.Generate(spec), Utf8Bom);

            request.Log?.WriteLine($"Building the installer with {request.Toolset.Source}");
            var environment = new Dictionary<string, string?> { [SignFileRequest.PasswordVariable] = request.Signing?.Password };
            List<string> arguments = ["-V2", "-WX", "-NOCONFIG", "-INPUTCHARSET", "UTF8"];
            if (OperatingSystem.IsWindows())
            {
                // Elsewhere makensis writes UTF-8 under the UTF-8 locale it runs with; the option exists only on Windows.
                arguments.AddRange(["-OUTPUTCHARSET", "UTF8"]);
            }

            arguments.Add(ScriptFileName);
            var (exitCode, output) = request.Toolset.Run(arguments, staging, environment);
            var built = Path.Combine(staging, NsisInstallerSpec.OutputFileName);
            if (exitCode != 0 || !File.Exists(built))
            {
                keep = true;
                throw new NsisException(
                    $"makensis failed with exit code {exitCode}. The script and staged files are kept in {staging}.\n{output.TrimEnd()}");
            }

            Directory.CreateDirectory(outputDirectory);
            var installer = Path.Combine(outputDirectory, GetOutputFileName(config.App, architecture));
            File.Move(built, installer, overwrite: true);
            return new NsisPackResult(installer, spec, signed, keep ? staging : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new NsisException($"Cannot build the installer: {ex.Message}", ex);
        }
        catch (SigningException ex)
        {
            throw new NsisException($"Signing failed: {ex.Message}", ex);
        }
        finally
        {
            if (!keep)
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    request.Log?.WriteLine($"note: could not delete the temporary directory {staging}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>An ICO file starts with reserved 0, type 1 (icon) and a non-zero image count, all little-endian.</summary>
    internal static bool IsIco(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[6];
        return stream.ReadAtLeast(header, 6, throwOnEndOfStream: false) == 6
            && header[0] == 0 && header[1] == 0 && header[2] == 1 && header[3] == 0
            && (header[4] != 0 || header[5] != 0);
    }

    /// <summary>
    /// Copies the input directory, including hidden files, skipping <paramref name="exclude"/> (the
    /// output directory, when it lies inside the input). Returns the total size and the empty directories.
    /// </summary>
    private static (long Size, IReadOnlyList<string> EmptyDirectories) CopyDirectory(string source, string destination, string exclude)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = false };
        long size = 0;
        var empty = new List<string>();
        var excludePrefix = exclude.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        void Copy(string from, string to, string relative)
        {
            Directory.CreateDirectory(to);
            var any = false;
            foreach (var file in Directory.EnumerateFiles(from, "*", options).Order(StringComparer.Ordinal))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
                size += new FileInfo(file).Length;
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
        return (size, empty);
    }

    private static List<string> SignApplicationFiles(
        string appDirectory, string main, WindowsSignFiles which, NsisSigning signing, AppConfig app, TextWriter? log)
    {
        IEnumerable<string> candidates = which == WindowsSignFiles.All
            ? Directory.EnumerateFiles(appDirectory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
            : [Path.Combine(appDirectory, main.Replace('/', Path.DirectorySeparatorChar))];

        var options = new SigningOptions
        {
            Credential = signing.Credential,
            TimestampUrl = signing.Timestamp,
            Description = app.Name,
            DescriptionUrl = app.Url,
        };
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
    /// Copies the license next to the script. A UTF-8 text file without a byte order mark gets one,
    /// because NSIS reads such files in the system code page otherwise.
    /// </summary>
    private static string StageLicense(string path, string staging)
    {
        var rtf = path.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase);
        var name = rtf ? "license.rtf" : "license.txt";
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.AsSpan().StartsWith("﻿"u8) || bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]);
        if (!rtf && !hasBom)
        {
            bytes = [.. Utf8Bom.Preamble, .. bytes];
        }

        File.WriteAllBytes(Path.Combine(staging, name), bytes);
        return name;
    }

    /// <summary>
    /// Writes the signing request and a wrapper script that runs <c>zexoro sign-file</c>, and returns
    /// the command for <c>!finalize</c>. The wrapper holds the quoted paths, so the command in the
    /// NSIS script is a fixed ASCII string whatever the paths contain.
    /// </summary>
    private static string WriteSignCommand(string staging, NsisSigning signing, AppConfig app)
    {
        var requestPath = Path.Combine(staging, SignRequestFileName);
        new SignFileRequest
        {
            Certificate = signing.CertificatePath,
            Timestamp = signing.Timestamp,
            Description = app.Name,
            DescriptionUrl = app.Url,
        }.Write(requestPath);

        string[] arguments = [.. signing.ZexoroCommand, "sign-file", "--request", requestPath];
        if (OperatingSystem.IsWindows())
        {
            if (arguments.Any(a => a.Contains('"', StringComparison.Ordinal)))
            {
                throw new NsisException("Cannot sign: a path contains '\"'.");
            }

            var line = string.Join(' ', arguments.Select(a => "\"" + a.Replace("%", "%%", StringComparison.Ordinal) + "\""));
            File.WriteAllText(Path.Combine(staging, "sign.cmd"), $"@echo off\r\nchcp 65001 >nul\r\n{line} %1\r\nexit /b %ERRORLEVEL%\r\n", new UTF8Encoding(false));
            return "cmd.exe /d /c sign.cmd \"%1\"";
        }

        var command = string.Join(' ', arguments.Select(a => "'" + a.Replace("'", "'\\''", StringComparison.Ordinal) + "'"));
        File.WriteAllText(Path.Combine(staging, "sign.sh"), $"#!/bin/sh\nexec {command} \"$1\"\n", new UTF8Encoding(false));
        return "sh ./sign.sh \"%1\"";
    }
}
