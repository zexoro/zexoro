namespace Zexoro.Formats.Nsis;

/// <summary>Building an NSIS installer failed for a reason the user can act on; the message says what to do.</summary>
public sealed class NsisException : Exception
{
    public NsisException()
    {
    }

    public NsisException(string message)
        : base(message)
    {
    }

    public NsisException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
