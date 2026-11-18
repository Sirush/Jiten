using Jiten.Core.Data;
using StackExchange.Redis;

namespace Jiten.Parser.Data.Redis;

public interface IDeckWordCache
{
    Task<DeckWord?> GetAsync(DeckWordCacheKey key);

    /// <summary>Synchronous process-local hit; returns the stored instance itself, which callers must only read.</summary>
    bool TryGetShared(DeckWordCacheKey key, out DeckWord? shared)
    {
        shared = null;
        return false;
    }

    Task<Dictionary<DeckWordCacheKey, DeckWord?>> GetManyAsync(IReadOnlyList<DeckWordCacheKey> keys);
    Task SetAsync(DeckWordCacheKey key, DeckWord word, CommandFlags flags = CommandFlags.None);
    Task SetManyAsync(IReadOnlyList<(DeckWordCacheKey key, DeckWord word)> entries, CommandFlags flags = CommandFlags.None);
}

public record DeckWordCacheKey(string Text, PartOfSpeech PartOfSpeech, string DictionaryForm, string NormalizedForm, string Reading,
                               bool IsPersonNameContext, bool IsNameLikeSudachiNoun, bool IsImperative);
