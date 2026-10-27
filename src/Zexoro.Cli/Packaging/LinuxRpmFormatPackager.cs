using System.Globalization;
using Zexoro.Formats.Linux;
using Zexoro.Platforms;
using Zexoro.Signing;
using Zexoro.Updates;

namespace Zexoro.Cli.Packaging;

/// <summary>Linux <c>rpm</c>: an rpm package, signed with <c>[linux.rpm.signing]</c>, and with <c>[updates]</c> its release fragment.</summary>
internal sealed class LinuxRpmFormatPackager : IPackager
{
    /// <summary>The reproducible-builds variable that fixes the signature time (seconds since 1970).</summary>
    internal const string SourceDateEpoch = "SOURCE_DATE_EPOCH";

    public PackageFormat Format => PackageFormat.Rpm;

    public void Pack(PackContext context)
    {
        var config = context.Config;
        OpenPgpSigningKey? key = null;
        try
        {
            RpmHeaderSigner? signer = null;
            if (config.Linux.Rpm.Signing is { } signing)
            {
                var time = SignatureTime(context.Environment);
                try
                {
                    key = OpenPgpSigningKey.Load(signing.ResolveKey(context.Environment.GetVariable), signing.ResolvePassword(context.Environment.GetVariable));
                    key.CheckValidAt(time);
                }
                catch (SigningException ex)
                {
                    throw new PackException($"{signing.Key.Key}: {ex.Message}", ex);
                }

                var signingKey = key;
                signer = new RpmHeaderSigner(signingKey.Description, header => signingKey.SignBinary(header, time));
            }

            var result = RpmPackager.Pack(new RpmPackRequest
            {
                Config = config,
                Architecture = context.Target.Architecture,
                OutputDirectory = context.OutputDirectory,
                Log = context.Log,
                ResolveUpdater = runtime => LinuxTarFormatPackager.ResolveUpdater(runtime, context.Environment),
                Signer = signer,
            });
            context.Log.WriteLine($"Created {result.PackagePath}");
            if (config.Updates is { } updates)
            {
                ReleaseFragments.Write(
                    context,
                    updates,
                    result.PackagePath,
                    new UpdateTarget(OperatingSystemFamily.Linux.Name, result.Architecture.Name, RpmPackager.FormatName, RpmPackager.Scope),
                    RpmPackager.MinimumUpdater);
            }
        }
        catch (LinuxPackException ex)
        {
            throw new PackException(ex.Message, ex);
        }
        catch (SigningException ex)
        {
            throw new PackException($"Cannot sign the rpm: {ex.Message}", ex);
        }
        finally
        {
            key?.Dispose();
        }
    }

    /// <summary>
    /// The signature creation time: <c>SOURCE_DATE_EPOCH</c> when set, so signed packages can be
    /// reproduced (RSA PKCS #1 v1.5 signatures are deterministic), else the current time.
    /// </summary>
    internal static DateTimeOffset SignatureTime(CliEnvironment environment)
    {
        if (environment.GetVariable(SourceDateEpoch) is not { Length: > 0 } text)
        {
            return environment.Time.GetUtcNow();
        }

        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds <= uint.MaxValue
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : throw new PackException($"{SourceDateEpoch} must be a number of seconds since 1970-01-01 (UTC), but is '{text}'.");
    }
}
