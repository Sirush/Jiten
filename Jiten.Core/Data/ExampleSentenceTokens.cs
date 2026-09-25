using System.Buffers.Binary;

namespace Jiten.Core.Data;

/// <summary>A resolved word inside an example sentence; Position and Length index the sentence's plain Text.</summary>
public readonly record struct SentenceToken(
    int WordId,
    byte ReadingIndex,
    byte Position,
    byte Length,
    bool IsTarget,
    bool IsFunctionWord)
{
    public int WordKey => ExampleSentenceTokens.WordKey(WordId, ReadingIndex);
}

/// <summary>ExampleSentence.Tokens codec: version byte, then per token its little-endian WordKey, Position, and Length carrying the target and function-word bits.</summary>
public static class ExampleSentenceTokens
{
    public const byte FormatVersion = 1;
    public const int MaxWordId = (1 << 24) - 1;
    public const int MaxLength = 0x3F;

    /// <summary>Header bit of rows converted from the old link table: only the picked words are known, the rest of the sentence is not.</summary>
    public const byte PartialFlag = 0x80;

    private const int TokenSize = 6;
    private const byte FunctionWordBit = 0x40;
    private const byte TargetBit = 0x80;

    public static bool IsPartial(ReadOnlySpan<byte> bytes) => bytes.Length > 0 && (bytes[0] & PartialFlag) != 0;

    /// <summary>WordFormHelper.EncodeWordKey truncated to 32 bits; ids above 2^23 come out negative, which equality and GIN don't mind.</summary>
    public static int WordKey(int wordId, byte readingIndex) => unchecked((int)(((uint)wordId << 8) | readingIndex));

    public static (int WordId, byte ReadingIndex) FromWordKey(int key) => ((int)((uint)key >> 8), (byte)key);

    public static bool CanEncode(int wordId, int position, int length) =>
        wordId is >= 0 and <= MaxWordId && position is >= 0 and <= byte.MaxValue && length is > 0 and <= MaxLength;

    public static byte[] Encode(IReadOnlyList<SentenceToken> tokens, bool partial = false)
    {
        var bytes = new byte[1 + tokens.Count * TokenSize];
        bytes[0] = (byte)(FormatVersion | (partial ? PartialFlag : 0));

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!CanEncode(token.WordId, token.Position, token.Length))
                throw new ArgumentOutOfRangeException(nameof(tokens),
                    $"Token {token.WordId}:{token.ReadingIndex} at {token.Position}+{token.Length} does not fit the format");

            var slot = bytes.AsSpan(1 + i * TokenSize, TokenSize);
            BinaryPrimitives.WriteInt32LittleEndian(slot, token.WordKey);
            slot[4] = token.Position;
            slot[5] = (byte)(token.Length
                             | (token.IsFunctionWord ? FunctionWordBit : 0)
                             | (token.IsTarget ? TargetBit : 0));
        }

        return bytes;
    }

    public static SentenceToken[] Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || (bytes[0] & ~PartialFlag) != FormatVersion || (bytes.Length - 1) % TokenSize != 0)
            throw new InvalidDataException($"Unsupported sentence token payload (version {(bytes.Length > 0 ? bytes[0] : -1)}, {bytes.Length} bytes)");

        var tokens = new SentenceToken[(bytes.Length - 1) / TokenSize];
        for (int i = 0; i < tokens.Length; i++)
        {
            var slot = bytes.Slice(1 + i * TokenSize, TokenSize);
            var (wordId, readingIndex) = FromWordKey(BinaryPrimitives.ReadInt32LittleEndian(slot));
            byte flags = slot[5];
            tokens[i] = new SentenceToken(wordId, readingIndex, slot[4], (byte)(flags & MaxLength),
                                          (flags & TargetBit) != 0, (flags & FunctionWordBit) != 0);
        }

        return tokens;
    }

    public const int CoarseBucketCount = 64;
    public const int FineBucketCount = 4096;

    /// <summary>Bucket keys live below the smallest word key (WordId 1,000,000 &lt;&lt; 8), so they never match a word.</summary>
    public static int CoarseBucketKey(int coarseBucket) => coarseBucket;

    public static int FineBucketKey(int fineBucket) => CoarseBucketCount + fineBucket;

    public static bool IsBucketKey(int key) => key is >= 0 and < CoarseBucketCount + FineBucketCount;

    public static int? FineBucketOf(int[]? wordKeys)
    {
        if (wordKeys == null) return null;
        foreach (var key in wordKeys)
            if (key is >= CoarseBucketCount and < CoarseBucketCount + FineBucketCount)
                return key - CoarseBucketCount;
        return null;
    }

    /// <summary>GIN-indexed inverse of Tokens (every token, pick or not) plus the sentence's random sampling buckets.</summary>
    public static int[] WordKeys(IEnumerable<SentenceToken> tokens, int fineBucket)
    {
        var keys = new SortedSet<int>
        {
            CoarseBucketKey(fineBucket / (FineBucketCount / CoarseBucketCount)),
            FineBucketKey(fineBucket)
        };
        foreach (var token in tokens)
            keys.Add(token.WordKey);
        return keys.ToArray();
    }

    /// <summary>The token standing for the form, preferring the sentence's pick over a later plain occurrence.</summary>
    public static SentenceToken? FindForm(IReadOnlyList<SentenceToken> tokens, int wordId, byte readingIndex)
    {
        SentenceToken? first = null;
        foreach (var token in tokens)
        {
            if (token.WordId != wordId || token.ReadingIndex != readingIndex) continue;
            if (token.IsTarget) return token;
            first ??= token;
        }

        return first;
    }
}
