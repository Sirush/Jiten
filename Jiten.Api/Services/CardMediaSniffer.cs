using Jiten.Core.Data.User;

namespace Jiten.Api.Services;

/// <summary>
/// Detects card-media type from the file's leading bytes. Content-Type and file extension are never
/// trusted; only these signatures decide whether a file is accepted and whether it is an image or audio.
/// </summary>
public static class CardMediaSniffer
{
    public record Sniffed(CardMediaKind Kind, string Extension, string ContentType);

    /// <summary>Returns the detected media descriptor, or null when the bytes match no accepted format.</summary>
    public static Sniffed? Detect(byte[] bytes)
    {
        if (bytes.Length < 12)
            return null;

        // --- Images ---
        if (StartsWith(bytes, 0xFF, 0xD8, 0xFF))
            return new Sniffed(CardMediaKind.Image, "jpg", "image/jpeg");

        if (StartsWith(bytes, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A))
            return new Sniffed(CardMediaKind.Image, "png", "image/png");

        if (Ascii(bytes, 0, "GIF87a") || Ascii(bytes, 0, "GIF89a"))
            return new Sniffed(CardMediaKind.Image, "gif", "image/gif");

        // RIFF....WEBP
        if (Ascii(bytes, 0, "RIFF") && Ascii(bytes, 8, "WEBP"))
            return new Sniffed(CardMediaKind.Image, "webp", "image/webp");

        // HEIC/AVIF share m4a's "ftyp" box, so they must be refused before the audio branch; browsers send them as JPEG.
        if (Ascii(bytes, 4, "ftyp") && IsHeifImage(bytes))
            return null;

        // --- Audio ---
        // MP3: ID3 tag or an MPEG audio frame sync (0xFF followed by 0xE0-set bits).
        if (Ascii(bytes, 0, "ID3") || (bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0))
            return new Sniffed(CardMediaKind.Audio, "mp3", "audio/mpeg");

        // Ogg container (covers .ogg vorbis and .opus).
        if (Ascii(bytes, 0, "OggS"))
            return new Sniffed(CardMediaKind.Audio, "ogg", "audio/ogg");

        // WAV: RIFF....WAVE (the WEBP RIFF variant is matched above, so this only sees genuine audio).
        if (Ascii(bytes, 0, "RIFF") && Ascii(bytes, 8, "WAVE"))
            return new Sniffed(CardMediaKind.Audio, "wav", "audio/wav");

        // FLAC (native stream).
        if (Ascii(bytes, 0, "fLaC"))
            return new Sniffed(CardMediaKind.Audio, "flac", "audio/flac");

        // ISO-BMFF / MP4 audio (.m4a): "ftyp" box at offset 4 that wasn't a HEIF-family image above.
        if (Ascii(bytes, 4, "ftyp"))
            return new Sniffed(CardMediaKind.Audio, "m4a", "audio/mp4");

        // Matroska / WebM (audio): EBML header.
        if (StartsWith(bytes, 0x1A, 0x45, 0xDF, 0xA3))
            return new Sniffed(CardMediaKind.Audio, "webm", "audio/webm");

        return null;
    }

    // Sequence brands (heim/heis/avis) count too: ImageMagick would still hand them to libheif.
    private static readonly HashSet<string> HeifBrands = new(StringComparer.Ordinal)
    {
        "heic", "heix", "heim", "heis", "hevc", "hevx", "heif", "mif1", "msf1", "avif", "avis"
    };

    /// <summary>True when the ftyp box (major brand plus compatible brands) names any HEIF-family image brand.</summary>
    private static bool IsHeifImage(byte[] bytes)
    {
        // ftyp box: [size:4][ 'ftyp':4 ][ major_brand:4 ][ minor_version:4 ][ compatible_brands:4*n ].
        var boxSize = (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
        var end = boxSize is > 8 and <= 4096 ? Math.Min(boxSize, bytes.Length) : bytes.Length;

        for (var offset = 8; offset + 4 <= end; offset += 4)
        {
            if (offset == 12)
                continue;
            var brand = new string(new[] { (char)bytes[offset], (char)bytes[offset + 1], (char)bytes[offset + 2], (char)bytes[offset + 3] });
            if (HeifBrands.Contains(brand))
                return true;
        }

        return false;
    }

    private static bool StartsWith(byte[] bytes, params byte[] prefix)
    {
        if (bytes.Length < prefix.Length)
            return false;
        for (var i = 0; i < prefix.Length; i++)
            if (bytes[i] != prefix[i])
                return false;
        return true;
    }

    private static bool Ascii(byte[] bytes, int offset, string ascii)
    {
        if (offset + ascii.Length > bytes.Length)
            return false;
        for (var i = 0; i < ascii.Length; i++)
            if (bytes[offset + i] != (byte)ascii[i])
                return false;
        return true;
    }
}
