using Zexoro.Toml;

namespace Zexoro.Configuration;

/// <summary>
/// A string value that is either written literally or read from an environment variable
/// (<c>{ env = "NAME" }</c>). Environment variables are read when the value is used, not when the
/// configuration is loaded, so a configuration with unset variables still loads.
/// </summary>
public sealed class ConfigValue
{
    internal ConfigValue(string key, string? literal, string? environmentVariable, string source, TomlPosition position)
    {
        Key = key;
        Literal = literal;
        EnvironmentVariable = environmentVariable;
        Source = source;
        Position = position;
    }

    /// <summary>The dotted key, e.g. <c>windows.signing.password</c>.</summary>
    public string Key { get; }

    /// <summary>The literal value, or null for an environment variable reference.</summary>
    public string? Literal { get; }

    /// <summary>The environment variable name, or null for a literal value.</summary>
    public string? EnvironmentVariable { get; }

    /// <summary>Whether the value is read from an environment variable.</summary>
    public bool IsEnvironmentReference => EnvironmentVariable is not null;

    /// <summary>The configuration file the value is written in.</summary>
    public string Source { get; }

    /// <summary>Where the value is written.</summary>
    public TomlPosition Position { get; }

    /// <summary>
    /// Returns the literal value, or the environment variable's value.
    /// </summary>
    /// <param name="environment">Looks up environment variables; defaults to the process environment.</param>
    /// <exception cref="ConfigurationException">The environment variable is not set or is empty.</exception>
    public string Resolve(Func<string, string?>? environment = null)
    {
        if (Literal is not null)
        {
            return Literal;
        }

        var value = (environment ?? Environment.GetEnvironmentVariable)(EnvironmentVariable!);
        return string.IsNullOrEmpty(value)
            ? throw new ConfigurationException([new ConfigError(
                $"{Key}: the environment variable '{EnvironmentVariable}' is not set", Source, Position)])
            : value;
    }

    /// <summary>Describes the value without revealing a literal (which may be a secret).</summary>
    public override string ToString() =>
        IsEnvironmentReference ? $"{{ env = \"{EnvironmentVariable}\" }}" : "(literal value)";
}
