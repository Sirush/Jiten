using System.Buffers;
using System.Buffers.Binary;

namespace Jiten.Core.Data;

/// <summary>A decoded sentence profile: sentence i holds the local word ids Ids[Starts[i]..Starts[i+1]), each mapping to Keys[id].</summary>
public sealed class SentenceProfile
{
    public required int[] Keys { get; init; }
    public required int[] Starts { get; init; }
    public required int[] Ids { get; init; }

    public int SentenceCount => Starts.Length - 1;

    public ReadOnlySpan<int> Sentence(int index) => Ids.AsSpan(Starts[index], Starts[index + 1] - Starts[index]);

    public static readonly SentenceProfile Empty = new() { Keys = [], Starts = [0], Ids = [] };
}

/// <summary>
/// Packs each sentence's distinct content-word keys (<see cref="ExampleSentenceTokens.WordKey"/>): version byte, varint word count,
/// the int32 keys ordered by sentence frequency, varint sentence count, then per sentence a varint length and its sorted local ids delta-coded.
/// </summary>
public static class SentenceProfileCodec
{
    public const byte FormatVersion = 1;

    public static byte[] Encode(IReadOnlyList<IReadOnlyCollection<int>> sentences)
    {
        var frequency = new Dictionary<int, int>();
        foreach (var sentence in sentences)
        foreach (var key in sentence)
            frequency[key] = frequency.GetValueOrDefault(key) + 1;

        // Most frequent first, so the words most sentences share get the one-byte ids.
        var keys = frequency.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key).ToArray();
        var localIds = new Dictionary<int, int>(keys.Length);
        for (int i = 0; i < keys.Length; i++)
            localIds[keys[i]] = i;

        var writer = new ArrayBufferWriter<byte>(16 + keys.Length * 4 + sentences.Count * 8);
        writer.GetSpan(1)[0] = FormatVersion;
        writer.Advance(1);
        WriteVarint(writer, (uint)keys.Length);
        foreach (var key in keys)
        {
            BinaryPrimitives.WriteInt32LittleEndian(writer.GetSpan(4), key);
            writer.Advance(4);
        }

        WriteVarint(writer, (uint)sentences.Count);
        var ids = new List<int>();
        foreach (var sentence in sentences)
        {
            ids.Clear();
            foreach (var key in sentence)
                ids.Add(localIds[key]);
            ids.Sort();

            WriteVarint(writer, (uint)ids.Count);
            int previous = 0;
            foreach (var id in ids)
            {
                WriteVarint(writer, (uint)(id - previous));
                previous = id;
            }
        }

        return writer.WrittenSpan.ToArray();
    }

    public static SentenceProfile Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return SentenceProfile.Empty;
        if (bytes[0] != FormatVersion)
            throw new InvalidDataException($"Unknown sentence profile version {bytes[0]}");

        int offset = 1;
        int wordCount = (int)ReadVarint(bytes, ref offset);

        // A word merge can leave one key under two local ids; both resolve to the first so a sentence never counts it twice.
        var keys = new List<int>(wordCount);
        var canonical = new int[wordCount];
        var seen = new Dictionary<int, int>(wordCount);
        bool hasDuplicates = false;
        for (int i = 0; i < wordCount; i++)
        {
            int key = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4));
            offset += 4;
            if (seen.TryGetValue(key, out var existing))
            {
                canonical[i] = existing;
                hasDuplicates = true;
                continue;
            }

            canonical[i] = keys.Count;
            seen[key] = keys.Count;
            keys.Add(key);
        }

        int sentenceCount = (int)ReadVarint(bytes, ref offset);
        var starts = new int[sentenceCount + 1];
        var ids = new List<int>(sentenceCount * 6);
        for (int s = 0; s < sentenceCount; s++)
        {
            starts[s] = ids.Count;
            int length = (int)ReadVarint(bytes, ref offset);
            int previous = 0;
            for (int j = 0; j < length; j++)
            {
                previous += (int)ReadVarint(bytes, ref offset);
                int id = canonical[previous];
                if (hasDuplicates && ids.IndexOf(id, starts[s]) >= 0) continue;
                ids.Add(id);
            }
        }

        starts[sentenceCount] = ids.Count;
        return new SentenceProfile { Keys = keys.ToArray(), Starts = starts, Ids = ids.ToArray() };
    }

    public static int ReadSentenceCount(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return 0;
        int offset = 1;
        int wordCount = (int)ReadVarint(bytes, ref offset);
        offset += wordCount * 4;
        return (int)ReadVarint(bytes, ref offset);
    }

    /// <summary>Sentences with no unknown word and with exactly one, read straight off the bytes without building a profile.</summary>
    public static (int Total, int Readable, int OneUnknown) CountReadable(ReadOnlySpan<byte> bytes, Func<int, bool> isKnown)
    {
        if (bytes.Length == 0) return (0, 0, 0);
        if (bytes[0] != FormatVersion)
            throw new InvalidDataException($"Unknown sentence profile version {bytes[0]}");

        int offset = 1;
        int wordCount = (int)ReadVarint(bytes, ref offset);
        var keys = new int[wordCount];
        var known = new bool[wordCount];
        for (int i = 0; i < wordCount; i++)
        {
            keys[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4));
            known[i] = isKnown(keys[i]);
            offset += 4;
        }

        int sentenceCount = (int)ReadVarint(bytes, ref offset);
        int readable = 0, oneUnknown = 0;
        for (int s = 0; s < sentenceCount; s++)
        {
            int length = (int)ReadVarint(bytes, ref offset);
            int previous = 0, firstUnknownKey = 0, unknown = 0;
            for (int j = 0; j < length; j++)
            {
                previous += (int)ReadVarint(bytes, ref offset);
                if (known[previous]) continue;
                // A merged key can sit under two ids; the same key twice is still one unknown word.
                if (unknown == 1 && keys[previous] == firstUnknownKey) continue;
                if (unknown == 0) firstUnknownKey = keys[previous];
                unknown++;
            }

            if (unknown == 0) readable++;
            else if (unknown == 1) oneUnknown++;
        }

        return (sentenceCount, readable, oneUnknown);
    }

    /// <summary>The sentences as word-key arrays, in order.</summary>
    public static IEnumerable<int[]> Sentences(SentenceProfile profile)
    {
        for (int s = 0; s < profile.SentenceCount; s++)
        {
            var sentence = profile.Sentence(s);
            var keys = new int[sentence.Length];
            for (int j = 0; j < sentence.Length; j++)
                keys[j] = profile.Keys[sentence[j]];
            yield return keys;
        }
    }

    /// <summary>
    /// At most <paramref name="maxSentences"/> sentences taken at even intervals across the parts read in order, so each part
    /// contributes in proportion to its size; everything when the parts hold fewer.
    /// </summary>
    public static byte[] Sample(IReadOnlyList<SentenceProfile> parts, int maxSentences)
    {
        long total = parts.Sum(p => (long)p.SentenceCount);
        var picked = new List<IReadOnlyCollection<int>>((int)Math.Min(total, maxSentences));
        if (total == 0) return Encode(picked);

        int take = (int)Math.Min(total, maxSentences);
        int partIndex = 0;
        long partStart = 0;
        for (int j = 0; j < take; j++)
        {
            long target = take == total ? j : (long)((j + 0.5) * total / take);
            while (target >= partStart + parts[partIndex].SentenceCount)
            {
                partStart += parts[partIndex].SentenceCount;
                partIndex++;
            }

            var part = parts[partIndex];
            var sentence = part.Sentence((int)(target - partStart));
            var keys = new int[sentence.Length];
            for (int k = 0; k < sentence.Length; k++)
                keys[k] = part.Keys[sentence[k]];
            picked.Add(keys);
        }

        return Encode(picked);
    }

    private static void WriteVarint(ArrayBufferWriter<byte> writer, uint value)
    {
        var span = writer.GetSpan(5);
        int i = 0;
        while (value >= 0x80)
        {
            span[i++] = (byte)(value | 0x80);
            value >>= 7;
        }

        span[i++] = (byte)value;
        writer.Advance(i);
    }

    private static uint ReadVarint(ReadOnlySpan<byte> bytes, ref int offset)
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            byte b = bytes[offset++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
    }
}
