namespace Zexoro.Core.Tests;

public class ProductInfoTests
{
    [Fact]
    public void Version_StartsWithVersionPrefix()
    {
        Assert.StartsWith("0.2.0", ProductInfo.Version, StringComparison.Ordinal);
    }
}
