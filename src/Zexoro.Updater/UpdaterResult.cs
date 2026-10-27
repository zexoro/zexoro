using System.Text.Json;
using Zexoro.Updates;

namespace Zexoro.Updater;

/// <summary>Exit codes (docs/design/0005-nsis-updates.md section 3).</summary>
internal static class UpdaterExitCodes
{
    /// <summary>Update available, downloaded or applied.</summary>
    public const int Success = 0;

    /// <summary>Up to date, or nothing staged.</summary>
    public const int UpToDate = 1;

    public const int UsageError = 2;

    /// <summary>Network or HTTP failure.</summary>
    public const int Network = 3;

    /// <summary>Signature, sha256, size, replay, wrong app or channel, insecure URL.</summary>
    public const int VerificationFailed = 4;

    /// <summary>Apply failed; the install is unchanged.</summary>
    public const int ApplyFailed = 5;

    /// <summary>UAC declined, or the application did not exit in time.</summary>
    public const int Cancelled = 6;

    /// <summary>The user must update manually: the feed or install record is too new, or every newer release needs a newer updater.</summary>
    public const int ManualUpdateNeeded = 7;

    /// <summary>Apply failed and rolling back failed too: the install needs a reinstall.</summary>
    public const int RollbackFailed = 8;

    /// <summary>The exit code for a failed update step.</summary>
    public static int For(UpdateErrorKind kind) => kind switch
    {
        UpdateErrorKind.Network => Network,
        UpdateErrorKind.UpdaterTooOld or UpdateErrorKind.InvalidInstallRecord => ManualUpdateNeeded,
        _ => VerificationFailed,
    };
}

/// <summary>What a command reports: one JSON object with <c>--json</c>, otherwise a sentence.</summary>
internal sealed record UpdaterResult(int ExitCode, string Status, string Message)
{
    public const string Available = "available";
    public const string UpToDate = "up-to-date";
    public const string Downloaded = "downloaded";
    public const string Applied = "applied";
    public const string Error = "error";

    public string? Installed { get; init; }

    public string? Version { get; init; }

    public string? Notes { get; init; }

    public long? Size { get; init; }

    /// <summary>True when another updater process already reported the outcome (the copy that applied it).</summary>
    public bool Relayed { get; init; }

    public static UpdaterResult Fail(int exitCode, string message, string? installed = null) =>
        new(exitCode, Error, message) { Installed = installed };

    public byte[] ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", Status);
            WriteOptional(writer, "installed", Installed);
            WriteOptional(writer, "version", Version);
            WriteOptional(writer, "notes", Notes);
            if (Size is { } size)
            {
                writer.WriteNumber("size", size);
            }
            else
            {
                writer.WriteNull("size");
            }

            WriteOptional(writer, "error", Status == Error ? Message : null);
            writer.WriteNumber("exitCode", ExitCode);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteOptional(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }
}
