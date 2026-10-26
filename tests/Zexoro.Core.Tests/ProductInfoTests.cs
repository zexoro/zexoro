namespace Zexoro.Core.Tests;

public class ProductInfoTests
{
    [Fact]
    public void Version_StartsWithVersionPrefix()
    {
        Assert.StartsWith("0.1.0", ProductInfo.Version, StringComparison.Ordinal);
    }
}
