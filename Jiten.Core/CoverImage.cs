using ImageMagick;

namespace Jiten.Core;

public static class CoverImage
{
    /// <summary>Longest stored edge in pixels; the CDN optimizer derives every display size from this copy.</summary>
    public const int MaxEdge = 1024;

    /// <summary>High enough that the optimizer's re-encode doesn't compound visible loss; above ~92 the file grows with no visible gain.</summary>
    private const int JpegQuality = 92;

    /// <summary>Shrinks to fit <see cref="MaxEdge"/> without ever upscaling, flattens transparency onto white and re-encodes as JPEG.</summary>
    public static byte[] Normalize(byte[] source)
    {
        using var image = new MagickImage(source);
        image.AutoOrient();
        image.Resize(new MagickGeometry(MaxEdge, MaxEdge) { Greater = true });
        image.BackgroundColor = MagickColors.White;
        image.Alpha(AlphaOption.Remove);
        image.Strip();
        image.Quality = JpegQuality;
        image.Format = MagickFormat.Jpeg;
        return image.ToByteArray();
    }
}
