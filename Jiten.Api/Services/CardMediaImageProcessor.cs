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
            using var frames = ReadFrames(bytes, extension, logger);

            if (frames is null || frames.Count == 0)
            {
                logger?.LogWarning("Card-media image ({Extension}, {Bytes} bytes) decoded to no frames; "
                                   + "storing the original file unmodified.", extension, bytes.Length);
                return original;
            }

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
            logger?.LogWarning(ex, "Card-media image normalization failed ({Extension}, {Bytes} bytes); "
                                   + "storing the original file unmodified.", extension, bytes.Length);
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

    /// <summary>
    /// The default PNG reader returns only an APNG's first frame, so the APNG coder is named explicitly -
    /// but only for input that actually is one. On Linux that coder is a delegate to an ffmpeg binary the API
    /// image does not carry, and it then yields no frames at all rather than failing; forcing it on every PNG
    /// is what stored plain screenshots raw at full resolution.
    /// </summary>
    /// <returns>Null for an APNG this runtime cannot decode as an animation, which must keep its original file.</returns>
    private static MagickImageCollection? ReadFrames(byte[] bytes, string extension, ILogger? logger)
    {
        if (extension != "png" || !HasAnimationControlChunk(bytes))
            return new MagickImageCollection(bytes);

        MagickImageCollection? animated = null;
        try
        {
            animated = new MagickImageCollection(bytes, new MagickReadSettings { Format = MagickFormat.APng });
            if (animated.Count > 0)
                return animated;
        }
        catch (MagickException ex)
        {
            logger?.LogWarning(ex, "APNG decode failed.");
        }

        animated?.Dispose();

        // Reading it as a plain PNG would succeed and silently turn the animation into a single frame, so an
        // undecodable APNG keeps its original file instead.
        logger?.LogWarning("APNG could not be decoded as an animation; storing it unmodified.");
        return null;
    }

    /// <summary>True when the PNG carries an acTL chunk, which is what makes it an APNG.</summary>
    private static bool HasAnimationControlChunk(byte[] bytes)
    {
        // Chunks follow the 8-byte signature as [length:4][type:4][data][crc:4]. acTL always precedes IDAT,
        // so the scan stops at the first one rather than walking the whole pixel stream.
        var offset = 8;
        while (offset + 8 <= bytes.Length)
        {
            var length = ((long)bytes[offset] << 24) | ((long)bytes[offset + 1] << 16)
                                                     | ((long)bytes[offset + 2] << 8) | bytes[offset + 3];

            if (IsChunkType(bytes, offset + 4, "acTL")) return true;
            if (IsChunkType(bytes, offset + 4, "IDAT")) return false;

            var next = offset + 12L + length;
            if (next <= offset || next > bytes.Length) return false;
            offset = (int)next;
        }

        return false;
    }

    private static bool IsChunkType(byte[] bytes, int offset, string type)
    {
        for (var i = 0; i < 4; i++)
            if (bytes[offset + i] != (byte)type[i])
                return false;
        return true;
    }
}
