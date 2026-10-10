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

        // Matroska / WebM. An Anki image field can contain an animated WebM, so distinguish it from
        // audio WebM by its TrackType.
        if (StartsWith(bytes, 0x1A, 0x45, 0xDF, 0xA3))
        {
            if (ContainsVideoTrack(bytes))
                return new Sniffed(CardMediaKind.Image, "webm", "video/webm");
            return new Sniffed(CardMediaKind.Audio, "webm", "audio/webm");
        }

        return null;
    }

    // Only descend Segment -> Tracks -> TrackEntry; padding and media packets can contain the same bytes.
    private static bool ContainsVideoTrack(ReadOnlySpan<byte> bytes, ulong parent = 0)
    {
        while (!bytes.IsEmpty)
        {
            if (!ReadEbmlInteger(ref bytes, elementId: true, out var id, out _)
                || !ReadEbmlInteger(ref bytes, elementId: false, out var size, out var unknownSize))
                return false;

            if (unknownSize)
            {
                if (id != 0x18538067) return false;
                size = (ulong)bytes.Length;
            }
            if (size > (ulong)bytes.Length) return false;

            var data = bytes[..(int)size];
            if ((parent == 0 && id == 0x18538067
                 || parent == 0x18538067 && id == 0x1654AE6B
                 || parent == 0x1654AE6B && id == 0xAE)
                && ContainsVideoTrack(data, id))
                return true;

            if (parent == 0xAE && id == 0x83 && data.Length is >= 1 and <= 8
                && data[^1] == 1 && data[..^1].IndexOfAnyExcept((byte)0) < 0)
                return true;

            bytes = bytes[(int)size..];
        }

        return false;
    }

    private static bool ReadEbmlInteger(ref ReadOnlySpan<byte> bytes, bool elementId, out ulong value, out bool unknownSize)
    {
        value = 0;
        unknownSize = false;
        if (bytes.IsEmpty || bytes[0] == 0) return false;

        var length = 1;
        var marker = 0x80;
        while ((bytes[0] & marker) == 0)
        {
            marker >>= 1;
            length++;
        }
        if (length > (elementId ? 4 : 8) || length > bytes.Length) return false;

        value = elementId ? bytes[0] : (ulong)(bytes[0] & (marker - 1));
        for (var i = 1; i < length; i++) value = (value << 8) | bytes[i];
        unknownSize = !elementId && value == (1UL << (7 * length)) - 1;
        bytes = bytes[length..];
        return true;
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
