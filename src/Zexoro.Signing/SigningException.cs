namespace Zexoro.Signing;

/// <summary>Signing failed: the file, the certificate or the timestamp authority could not be used.</summary>
public sealed class SigningException : Exception
{
    public SigningException()
    {
    }

    public SigningException(string message)
        : base(message)
    {
    }

    public SigningException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
