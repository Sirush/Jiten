using FluentAssertions;
using ImageMagick;
using Jiten.Core;

namespace Jiten.Tests;

public class CoverImageTests
{
    private static byte[] MakeImage(uint width, uint height, MagickFormat format, MagickColor? colour = null)
    {
        using var image = new MagickImage(colour ?? MagickColors.Red, width, height);
        image.Format = format;
        return image.ToByteArray();
    }

    [Fact]
    public void Normalize_ShrinksLargeCoverToFitMaxEdge()
    {
        using var result = new MagickImage(CoverImage.Normalize(MakeImage(1500, 2250, MagickFormat.Png)));

        result.Format.Should().Be(MagickFormat.Jpeg);
        result.Height.Should().Be(CoverImage.MaxEdge);
        result.Width.Should().Be(683);
    }

    [Fact]
    public void Normalize_NeverUpscalesSmallCover()
    {
        using var result = new MagickImage(CoverImage.Normalize(MakeImage(272, 400, MagickFormat.Jpeg)));

        result.Width.Should().Be(272);
        result.Height.Should().Be(400);
    }

    [Fact]
    public void Normalize_FlattensTransparencyOntoWhite()
    {
        using var result = new MagickImage(CoverImage.Normalize(MakeImage(10, 10, MagickFormat.Png, MagickColors.Transparent)));

        var pixel = result.GetPixels().GetPixel(5, 5).ToColor()!;
        pixel.R.Should().BeGreaterThan(60000);
        pixel.G.Should().BeGreaterThan(60000);
        pixel.B.Should().BeGreaterThan(60000);
    }
}
