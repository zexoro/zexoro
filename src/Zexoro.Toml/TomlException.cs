namespace Zexoro.Toml;

/// <summary>A TOML document is not valid. The error points at the offending line and column.</summary>
public sealed class TomlException : Exception
{
    public TomlException()
        : this("The TOML document is not valid.", new TomlPosition(1, 1))
    {
    }

    public TomlException(string message)
        : this(message, new TomlPosition(1, 1))
    {
    }

    public TomlException(string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = message;
        Position = new TomlPosition(1, 1);
    }

    public TomlException(string reason, TomlPosition position)
        : base($"{reason} (at {position})")
    {
        Reason = reason;
        Position = position;
    }

    /// <summary>What is wrong, without the position.</summary>
    public string Reason { get; }

    /// <summary>Where the error was detected.</summary>
    public TomlPosition Position { get; }

    /// <summary>The 1-based line of the error.</summary>
    public int Line => Position.Line;

    /// <summary>The 1-based column of the error, in characters.</summary>
    public int Column => Position.Column;
}
