namespace Zexoro.Formats.Linux.Tests;

public sealed class PngIconTests
{
    private static byte[] SolidPng(int width, int height) => PngIcon.Encode(width, height, Enumerable.Repeat((byte)0x80, width * height * 4).ToArray());

    [Fact]
    public void FromBytes_Png_IsReturnedAsItIs()
    {
        var png = SolidPng(48, 32);

        var image = PngIcon.FromBytes(png);

        Assert.Same(png, image.Bytes);
        Assert.Equal((48, 32), (image.Width, image.Height));
    }

    [Fact]
    public void FromBytes_IcoWithBitmaps_ConvertsTheLargestToPng()
    {
        var image = PngIcon.FromBytes(TarFiles.Ico(16, 48, 32));

        Assert.Equal((48, 48), (image.Width, image.Height));
        var (width, height, rgba) = TarFiles.DecodePng(image.Bytes);
        Assert.Equal((48, 48), (width, height));
        Assert.Equal(0, rgba[3]); // (0, 0) is transparent
        var pixel = ((5 * 48) + 7) * 4; // (x 7, y 5): r = x, g = y, b = size
        Assert.Equal(new byte[] { 7, 5, 48, 255 }, rgba[pixel..(pixel + 4)]);
    }

    [Fact]
    public void FromBytes_Ico256_IsReadWithItsZeroSizeByte()
    {
        var image = PngIcon.FromBytes(TarFiles.Ico(32, 256));

        Assert.Equal((256, 256), (image.Width, image.Height));
    }

    [Fact]
    public void FromBytes_IcoWithPng_ReturnsTheStoredPng()
    {
        var png = SolidPng(64, 64);

        var image = PngIcon.FromBytes(TarFiles.IcoWithPng(png, 64));

        Assert.Equal(png, image.Bytes);
        Assert.Equal(64, image.Width);
    }

    [Fact]
    public void FromBytes_FixtureIcon_Converts()
    {
        // The e2e fixture's icon: one 16x16 32-bit bitmap.
        var path = Path.Combine(AppContext.BaseDirectory, "fixture-app.ico");
        var image = PngIcon.Load(path);

        Assert.Equal((16, 16), (image.Width, image.Height));
        Assert.True(PngIcon.IsPng(image.Bytes));
    }

    [Fact]
    public void FromBytes_EightBitBitmapWithMask_UsesPaletteAndMask()
    {
        // 2x2, 8 bits per pixel, palette of 2: index 1 everywhere, AND mask bit set for (1, 0).
        var bitmap = new byte[40 + 8 + (4 * 2) + (4 * 2)];
        BitConverter.GetBytes(40).CopyTo(bitmap, 0);
        BitConverter.GetBytes(2).CopyTo(bitmap, 4);
        BitConverter.GetBytes(4).CopyTo(bitmap, 8);
        BitConverter.GetBytes((ushort)1).CopyTo(bitmap, 12);
        BitConverter.GetBytes((ushort)8).CopyTo(bitmap, 14);
        BitConverter.GetBytes(2).CopyTo(bitmap, 32);
        bitmap[40 + 4] = 0x30; // palette[1] = b 0x30, g 0x20, r 0x10
        bitmap[40 + 5] = 0x20;
        bitmap[40 + 6] = 0x10;
        bitmap[48] = 1;
        bitmap[49] = 1;
        bitmap[52] = 1;
        bitmap[53] = 1;
        bitmap[56 + 4] = 0x40; // mask row of y = 0 (stored last): x = 1 transparent
        var ico = new byte[22 + bitmap.Length];
        ico[2] = 1;
        ico[4] = 1;
        ico[6] = 2;
        ico[7] = 2;
        BitConverter.GetBytes(bitmap.Length).CopyTo(ico, 14);
        BitConverter.GetBytes(22).CopyTo(ico, 18);
        bitmap.CopyTo(ico, 22);

        var (_, _, rgba) = TarFiles.DecodePng(PngIcon.FromBytes(ico).Bytes);

        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 255, 0x10, 0x20, 0x30, 0, 0x10, 0x20, 0x30, 255, 0x10, 0x20, 0x30, 255 }, rgba);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 0, 0, 1, 0, 0, 0 })]
    [InlineData(new byte[] { 0, 0, 1, 0, 1, 0, 16, 16, 0, 0, 1, 0, 32, 0, 0xFF, 0, 0, 0, 22, 0, 0, 0 })]
    public void FromBytes_Invalid_Throws(byte[] bytes) =>
        Assert.Throws<IconException>(() => PngIcon.FromBytes(bytes));
}
