using ImageMagick;
using Jiten.Core.Data.User;

namespace Jiten.Api.Services;

/// <summary>
/// Upload-time image normalization for card media. Every image (jpeg/png/webp/gif/heic/avif) is downscaled to
/// fit a 1600 px long edge (never upscaled), stripped of metadata (EXIF/GPS privacy), and re-encoded to WebP
/// q82. This converts HEIC/AVIF into a format every browser can render, and re-encodes multi-frame inputs
/// (animated GIF/WebP/APNG) to animated WebP with their frames and timing preserved. Audio and non-image kinds
/// pass through. Never throws: on failure the original bytes are returned so an upload can't fail because
/// normalization did.
/// </summary>
public static class CardMediaImageProcessor
{
    private const int MaxLongEdge = 1600;
    private const int WebpQuality = 82;
    // Hard cap on animation frames re-encoded. The 5 MB upload gate already bounds input, but coalescing a
    // pathological many-frame file is expensive; past this we keep the animation as-is rather than risk it.
    private const int MaxAnimationFrames = 300;

    public record Processed(byte[] Bytes, string Extension, string ContentType);

    public static Processed Normalize(
        CardMediaKind kind, string extension, string contentType, byte[] bytes, ILogger? logger = null)
    {
        var original = new Processed(bytes, extension, contentType);

        if (kind != CardMediaKind.Image)
            return original;

        try
        {
            // ImageMagick's default PNG reader returns only an APNG's first frame; the APNG coder must be
            // selected explicitly. It reads static PNGs as a single frame too, so it's safe for all PNG input.
            // GIF/WebP animation is read correctly under auto-detect.
            var readSettings = extension == "png"
                ? new MagickReadSettings { Format = MagickFormat.APng }
                : new MagickReadSettings();
            using var frames = new MagickImageCollection(bytes, readSettings);

            if (frames.Count == 0)
                return original;

            if (frames.Count > 1)
                return NormalizeAnimated(frames, original, logger);

            // Owned by `frames`; don't dispose it separately.
            var image = frames[0];
            Shrink(image);
            image.Strip();
            image.Quality = WebpQuality;
            image.Format = MagickFormat.WebP;
            return new Processed(image.ToByteArray(), "webp", "image/webp");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Card-media image normalization failed; storing the original file unmodified.");
            return original;
        }
    }

    // Re-encodes an animated GIF/WebP/APNG to animated WebP. Coalesce flattens each frame's dispose/blend into a
    // full canvas so per-frame resize can't smear the animation; frame delays survive the round-trip.
    private static Processed NormalizeAnimated(MagickImageCollection frames, Processed original, ILogger? logger)
    {
        if (frames.Count > MaxAnimationFrames)
        {
            logger?.LogInformation("Card-media animation has {Count} frames (> {Max}); storing it unmodified.",
                                    frames.Count, MaxAnimationFrames);
            return original;
        }

        frames.Coalesce();
        foreach (var frame in frames)
        {
            Shrink(frame);
            frame.Strip();
            frame.Quality = WebpQuality;
            frame.Format = MagickFormat.WebP;
        }

        // OptimizePlus restores inter-frame deltas so the output isn't a stack of full frames.
        frames.OptimizePlus();
        return new Processed(frames.ToByteArray(MagickFormat.WebP), "webp", "image/webp");
    }

    // Greater = the ImageMagick '>' flag: only shrink when larger than the box, preserving aspect.
    private static void Shrink(IMagickImage<ushort> image) =>
        image.Resize(new MagickGeometry(MaxLongEdge, MaxLongEdge) { Greater = true });
}
