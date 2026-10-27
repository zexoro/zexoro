namespace Zexoro.Configuration;

/// <summary>A configuration is not valid, or a value in it cannot be resolved. Holds every error found.</summary>
public sealed class ConfigurationException : Exception
{
    public ConfigurationException()
        : this("The configuration is not valid.")
    {
    }

    public ConfigurationException(string message)
        : base(message) => Errors = [];

    public ConfigurationException(string message, Exception innerException)
        : base(message, innerException) => Errors = [];

    public ConfigurationException(IEnumerable<ConfigError> errors)
        : this(errors.ToArray())
    {
    }

    private ConfigurationException(ConfigError[] errors)
        : base(errors.Length == 1
            ? errors[0].ToString()
            : $"The configuration has {errors.Length} errors:{Environment.NewLine}{string.Join(Environment.NewLine, errors.Select(e => e.ToString()))}")
        => Errors = errors;

    /// <summary>Every error, in document order. Empty when the exception was created from a message only.</summary>
    public IReadOnlyList<ConfigError> Errors { get; }
}
