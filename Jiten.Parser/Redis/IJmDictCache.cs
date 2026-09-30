using Jiten.Core.Data.JMDict;

namespace Jiten.Parser.Data.Redis;

public interface IJmDictCache
{
    Task<JmDictWord?> GetWordAsync(int wordId);
    Task<Dictionary<int, JmDictWord>> GetWordsAsync(IEnumerable<int> wordIds);

    Task<bool> SetWordAsync(int wordId, JmDictWord word);
    Task<bool> SetWordsAsync(Dictionary<int, JmDictWord> words);
    Task<bool> IsCacheInitializedAsync();
    Task SetCacheInitializedAsync();
}