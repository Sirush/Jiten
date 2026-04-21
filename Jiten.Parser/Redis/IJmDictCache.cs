using Jiten.Core.Data.JMDict;

namespace Jiten.Parser.Data.Redis;

public interface IJmDictCache
{
    // Task<List<int>> GetLookupIdsAsync(string key);
    // Task<Dictionary<string, List<int>>> GetLookupIdsAsync(IEnumerable<string> keys);
    Task<JmDictWord?> GetWordAsync(int wordId);
    Task<Dictionary<int, JmDictWord>> GetWordsAsync(IEnumerable<int> wordIds);

    // Task<bool> SetLookupIdsAsync(Dictionary<string, List<int>> lookups);
    Task<bool> SetWordAsync(int wordId, JmDictWord word);
    Task<bool> SetWordsAsync(Dictionary<int, JmDictWord> words);
    Task<bool> IsCacheInitializedAsync();
    Task SetCacheInitializedAsync();

    Task<Dictionary<int, List<string>>?> GetNonArchaicPosMapAsync();
    Task SetNonArchaicPosMapAsync(Dictionary<int, List<string>> map);

    Task PreloadWordsAsync(IEnumerable<int> wordIds);
    bool TryGetWordLocal(int wordId, out JmDictWord? word);
    JmDictWord?[]? GetWordArray();
}