using System.Buffers.Binary;

namespace Jiten.Core.Data.Billing;

/// <summary>5 bytes per entry (WordId int32 little-endian, then ReadingIndex); rank is the 1-based position.</summary>
public static class FrequencyListBlobPacker
{
    public const int EntrySize = 5;

    public static byte[] Pack(IReadOnlyList<(int WordId, byte ReadingIndex)> rankedWords)
    {
        var buffer = new byte[rankedWords.Count * EntrySize];
        for (var i = 0; i < rankedWords.Count; i++)
        {
            var offset = i * EntrySize;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), rankedWords[i].WordId);
            buffer[offset + 4] = rankedWords[i].ReadingIndex;
        }

        return buffer;
    }

    public static List<(int WordId, byte ReadingIndex)> Unpack(byte[]? blob)
    {
        if (blob is null || blob.Length < EntrySize) return [];

        var count = blob.Length / EntrySize;
        var result = new List<(int, byte)>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = i * EntrySize;
            result.Add((BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(offset, 4)), blob[offset + 4]));
        }

        return result;
    }

    /// <summary>Entries whose 1-based rank falls inside [minRank, maxRank]; null bounds are open ended.</summary>
    public static List<(int WordId, byte ReadingIndex, int Rank)> Slice(
        IReadOnlyList<(int WordId, byte ReadingIndex)> rankedWords, int? minRank, int? maxRank)
    {
        var from = Math.Max(1, minRank ?? 1);
        var to = Math.Min(rankedWords.Count, maxRank is > 0 ? maxRank.Value : rankedWords.Count);

        var result = new List<(int, byte, int)>();
        for (var rank = from; rank <= to; rank++)
        {
            var (wordId, readingIndex) = rankedWords[rank - 1];
            result.Add((wordId, readingIndex, rank));
        }

        return result;
    }

    public static HashSet<long> ToKeySet(IEnumerable<(int WordId, byte ReadingIndex, int Rank)> entries)
    {
        return entries.Select(e => ((long)e.WordId << 8) | e.ReadingIndex).ToHashSet();
    }

    public static HashSet<long> DecodeToKeySet(byte[]? blob)
    {
        return Unpack(blob).Select(e => ((long)e.WordId << 8) | e.ReadingIndex).ToHashSet();
    }
}
