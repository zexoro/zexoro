namespace Zexoro.Cli.Tests;

public sealed class AppTests : IDisposable
{
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public void Dispose()
    {
        _stdout.Dispose();
        _stderr.Dispose();
    }

    private int Run(params string[] args) => App.Run(args, _stdout, _stderr);

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public void Version_PrintsProductVersion(string flag)
    {
        Assert.Equal(ExitCodes.Success, Run(flag));
        Assert.Equal(ProductInfo.Version, _stdout.ToString().Trim());
        Assert.Empty(_stderr.ToString());
    }

    [Theory]
    [InlineData]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_PrintsUsage(params string[] args)
    {
        Assert.Equal(ExitCodes.Success, Run(args));
        Assert.Contains("Usage: zexoro", _stdout.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--version", "--help")]
    public void UnknownArguments_FailWithUsageError(params string[] args)
    {
        Assert.Equal(ExitCodes.UsageError, Run(args));
        Assert.Empty(_stdout.ToString());
        Assert.Contains("unrecognized arguments", _stderr.ToString(), StringComparison.Ordinal);
    }
}
