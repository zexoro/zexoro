namespace Zexoro.Toolsets;

/// <summary>A bundled toolset is missing, corrupt or cannot be extracted.</summary>
public sealed class ToolsetException : Exception
{
    public ToolsetException()
    {
    }

    public ToolsetException(string message)
        : base(message)
    {
    }

    public ToolsetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
