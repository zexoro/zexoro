using Zexoro.Configuration;
using Zexoro.Toml;

namespace Zexoro.Core.Tests;

public class ConfigValueTests
{
    private static readonly string BaseDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "zexoro-value-tests"));

    private static WindowsSigningConfig Signing(string lines)
    {
        var text = $"""
            [app]
            id = "com.example.notes"
            name = "Notes"
            version = "1.0.0"
            publisher = "Me"
            icon = "a.ico"

            [input]
            dir = "out"
            main = "Notes.exe"

            [windows.signing]
            {lines}
            """;
        return Assert.IsType<WindowsSigningConfig>(ZexoroConfig.Parse(text, BaseDirectory).Windows?.Signing);
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Resolve_Literal_ReturnsItWithoutReadingTheEnvironment()
    {
        var signing = Signing("certificate = \"cert.pfx\"\npassword = \"secret\"");

        Assert.False(signing.Certificate.IsEnvironmentReference);
        Assert.Null(signing.Certificate.EnvironmentVariable);
        Assert.Equal("secret", signing.ResolvePassword(_ => throw new InvalidOperationException("not expected")));
        Assert.Equal("(literal value)", signing.Password?.ToString());
    }

    [Fact]
    public void Resolve_EnvironmentReference_ReadsTheVariable()
    {
        var signing = Signing("certificate = { env = \"CERT\" }\npassword = { env = \"PASS\" }");

        Assert.Equal("windows.signing.password", signing.Password?.Key);
        Assert.Equal(new TomlPosition(14, 12), signing.Password?.Position);
        Assert.Equal("hunter2", signing.ResolvePassword(Env(("PASS", "hunter2"))));
        Assert.Equal("{ env = \"PASS\" }", signing.Password?.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_UnsetOrEmptyVariable_ThrowsWithPosition(bool empty)
    {
        var signing = Signing("certificate = \"c.pfx\"\npassword = { env = \"SIGN_PASSWORD\" }");
        var environment = empty ? Env(("SIGN_PASSWORD", "")) : Env();

        var ex = Assert.Throws<ConfigurationException>(() => signing.ResolvePassword(environment));

        var error = Assert.Single(ex.Errors);
        Assert.Equal("windows.signing.password: the environment variable 'SIGN_PASSWORD' is not set", error.Message);
        Assert.Equal("zexoro.toml", error.Source);
        Assert.Equal(new TomlPosition(14, 12), error.Position);
    }

    [Fact]
    public void Resolve_WithoutLookup_UsesProcessEnvironment()
    {
        var name = $"ZEXORO_TEST_{Guid.NewGuid():N}";
        var signing = Signing($"certificate = {{ env = \"{name}\" }}");
        Environment.SetEnvironmentVariable(name, "from-process");
        try
        {
            Assert.Equal("from-process", signing.Certificate.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void ResolveCertificatePath_RelativeValue_IsResolvedAgainstConfigDirectory()
    {
        var signing = Signing("certificate = { env = \"CERT\" }");

        Assert.Equal(Path.Combine(BaseDirectory, "certs", "sign.pfx"), signing.ResolveCertificatePath(Env(("CERT", "certs/sign.pfx"))));
        Assert.Equal(Path.Combine(BaseDirectory, "certs", "sign.pfx"), signing.ResolveCertificatePath(Env(("CERT", @"certs\sign.pfx"))));
    }

    [Fact]
    public void ResolveCertificatePath_AbsoluteValue_IsKept()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "sign.pfx");
        var signing = Signing("certificate = { env = \"CERT\" }");

        Assert.Equal(Path.GetFullPath(absolute), signing.ResolveCertificatePath(Env(("CERT", absolute))));
    }

    [Fact]
    public void ResolvePassword_NotConfigured_ReturnsNull()
    {
        Assert.Null(Signing("certificate = \"c.pfx\"").ResolvePassword(Env()));
    }

    [Fact]
    public void ResolveTimestamp_FromEnvironment_ValidatesUrl()
    {
        var signing = Signing("certificate = \"c.pfx\"\ntimestamp = { env = \"TSA\" }");

        Assert.Equal(new Uri("https://tsa.example.com/rfc3161"), signing.ResolveTimestamp(Env(("TSA", "https://tsa.example.com/rfc3161"))));
        var error = Assert.Single(Assert.Throws<ConfigurationException>(() => signing.ResolveTimestamp(Env(("TSA", "tsa.example.com")))).Errors);
        Assert.Equal("windows.signing.timestamp: 'tsa.example.com' is not an absolute http or https URL", error.Message);
        Assert.Equal(new TomlPosition(14, 13), error.Position);
    }

    [Fact]
    public void ResolveTimestamp_NotConfigured_ReturnsNull()
    {
        Assert.Null(Signing("certificate = \"c.pfx\"").ResolveTimestamp(Env()));
    }
}
