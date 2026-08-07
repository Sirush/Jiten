using System.Buffers.Binary;

namespace Jiten.Core.Data.FSRS;

public readonly record struct PackedReview(FsrsRating Rating, DateTime ReviewDateTime, int? ReviewDuration);

/// <param name="ReviewCount">True number of reviews, which exceeds the entries in <paramref name="Logs"/> when truncated.</param>
public readonly record struct PackResult(byte[]? Logs, DateTime? FirstReview, int ReviewCount, bool Truncated);

/// <summary>
/// Packs a card's review history into a single blob for <see cref="FsrsCardArchive"/>, roughly 13x smaller
/// than the equivalent rows and, more to the point, absent from the live log table and its index.
/// </summary>
public static class ReviewLogPacker
{
    public const byte FormatVersion = 1;

    /// <summary>Beyond this the most recent entries are kept and the truncated flag is set.</summary>
    public const int MaxEntries = 5000;

    private const int HeaderSize = 8;
    private const int EntrySize = 7;
    private const ushort NullDuration = 0xFFFF;
    private const ushort MaxDurationDs = 0xFFFE;
    private const byte FlagTruncated = 0x01;

    public static PackResult Pack(IEnumerable<FsrsReviewLog> logs, bool markTruncated = false)
        => Pack(logs.Select(l => new PackedReview(l.Rating, l.ReviewDateTime, l.ReviewDuration)), markTruncated);

    /// <param name="markTruncated">Set when the caller already knows history is missing from <paramref name="reviews"/>.</param>
    public static PackResult Pack(IEnumerable<PackedReview> reviews, bool markTruncated = false)
    {
        var ordered = reviews.OrderBy(r => r.ReviewDateTime).ToList();
        if (ordered.Count == 0)
            return new PackResult(null, null, 0, markTruncated);

        var total = ordered.Count;
        var truncated = markTruncated;

        if (ordered.Count > MaxEntries)
        {
            ordered = ordered.GetRange(ordered.Count - MaxEntries, MaxEntries);
            truncated = true;
        }

        var firstReview = AsUtc(ordered[0].ReviewDateTime);
        var entries = new List<(uint Delta, byte Rating, ushort DurationDs)>(ordered.Count);

        foreach (var review in ordered)
        {
            var deltaSeconds = (AsUtc(review.ReviewDateTime) - firstReview).TotalSeconds;
            if (deltaSeconds is < 0 or > uint.MaxValue)
            {
                truncated = true;
                continue;
            }

            var rating = (byte)review.Rating;
            if (rating is < (byte)FsrsRating.Again or > (byte)FsrsRating.Easy)
            {
                truncated = true;
                continue;
            }

            entries.Add(((uint)deltaSeconds, rating, EncodeDuration(review.ReviewDuration)));
        }

        if (entries.Count == 0)
            return new PackResult(null, null, total, true);

        var blob = new byte[HeaderSize + entries.Count * EntrySize];
        blob[0] = FormatVersion;
        blob[1] = truncated ? FlagTruncated : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), (uint)entries.Count);

        var offset = HeaderSize;
        foreach (var (delta, rating, durationDs) in entries)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(offset), delta);
            blob[offset + 4] = rating;
            BinaryPrimitives.WriteUInt16LittleEndian(blob.AsSpan(offset + 5), durationDs);
            offset += EntrySize;
        }

        return new PackResult(blob, firstReview, total, truncated);
    }

    /// <exception cref="InvalidDataException">
    /// The blob is not a readable v1 history. Callers must surface this as unrestorable rather than as an
    /// empty history, which would present data loss as success.
    /// </exception>
    public static List<PackedReview> Unpack(byte[] blob, DateTime firstReview)
    {
        if (blob.Length < HeaderSize)
            throw new InvalidDataException($"Review log blob is {blob.Length} bytes, shorter than the {HeaderSize}-byte header.");

        var version = blob[0];
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported review log format version {version}.");

        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(4));
        var expectedLength = (long)HeaderSize + (long)entryCount * EntrySize;
        if (expectedLength != blob.Length)
            throw new InvalidDataException($"Review log blob declares {entryCount} entries ({expectedLength} bytes) but is {blob.Length} bytes.");

        var basis = AsUtc(firstReview);
        var result = new List<PackedReview>((int)entryCount);
        var previousDelta = 0u;
        var offset = HeaderSize;

        for (var i = 0u; i < entryCount; i++)
        {
            var delta = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(offset));
            if (i > 0 && delta < previousDelta)
                throw new InvalidDataException($"Review log blob entry {i} goes backwards in time.");
            previousDelta = delta;

            var rating = blob[offset + 4];
            if (rating is < (byte)FsrsRating.Again or > (byte)FsrsRating.Easy)
                throw new InvalidDataException($"Review log blob entry {i} has invalid rating {rating}.");

            var durationDs = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(offset + 5));
            result.Add(new PackedReview((FsrsRating)rating, basis.AddSeconds(delta),
                                        durationDs == NullDuration ? null : durationDs * 100));

            offset += EntrySize;
        }

        return result;
    }

    public static bool IsTruncated(byte[]? blob) => blob is { Length: >= HeaderSize } && (blob[1] & FlagTruncated) != 0;

    private static ushort EncodeDuration(int? durationMs)
    {
        if (durationMs is not > 0)
            return NullDuration;
        var deciseconds = (durationMs.Value + 50) / 100;
        return deciseconds >= MaxDurationDs ? MaxDurationDs : (ushort)deciseconds;
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
