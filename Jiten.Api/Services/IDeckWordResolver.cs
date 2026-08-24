using Jiten.Api.Dtos;
using Jiten.Core.Data;
using Jiten.Core.Data.User;

namespace Jiten.Api.Services;

public record DeckWordResolveRequest(
    int DeckId,
    Deck Deck,
    DeckDownloadType DownloadType,
    DeckOrder Order,
    int MinFrequency,
    int MaxFrequency,
    bool ExcludeMatureMasteredBlacklisted,
    bool ExcludeAllTrackedWords,
    float? TargetPercentage,
    int? MinOccurrences = null,
    int? MaxOccurrences = null,
    string? PosFilter = null,
    bool StartFromKnown = false,
    MediaType? FrequencySource = null);

public class ResolvedWord
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public int Occurrences { get; set; }
    public int SortOrder { get; set; }
}

public record GlobalDynamicResult(List<ResolvedWord> Words, bool WasTruncated);

/// <summary>Which ranking a dynamic frequency deck reads from; both null means the site-wide ranking.</summary>
public readonly record struct FrequencyScope(MediaType? MediaType, long? FrequencyListId)
{
    public bool IsGlobal => MediaType is null && FrequencyListId is null;

    public static FrequencyScope From(UserStudyDeck studyDeck) =>
        new(studyDeck.FrequencyMediaType, studyDeck.FrequencyListId);
}

public interface IDeckWordResolver
{
    Task<(List<DeckWord>? Words, IResult? Error)> ResolveDeckWords(DeckWordResolveRequest request);
    Task<HashSet<long>> GetStudyDeckWordKeys(List<int> deckIds);
    Task<HashSet<long>> GetStaticDeckWordKeys(List<int> studyDeckIds);
    Task<GlobalDynamicResult> ResolveGlobalDynamicWords(int? minFreq, int? maxFreq, string? posFilter,
        bool excludeKana, bool excludeMatureMasteredBlacklisted, bool excludeAllTrackedWords,
        FrequencyScope scope = default);
    Task<List<ResolvedWord>> ResolveStaticDeckWords(int studyDeckId, int order,
        bool excludeMatureMasteredBlacklisted = false, bool excludeAllTrackedWords = false,
        DeckDownloadType downloadType = DeckDownloadType.Full,
        int minFrequency = 0, int maxFrequency = 0,
        int? minOccurrences = null, int? maxOccurrences = null,
        float? targetPercentage = null, bool startFromKnown = false);
    Task<HashSet<long>> GetGlobalDynamicWordKeys(int? minFreq, int? maxFreq, string? posFilter,
        FrequencyScope scope = default);
    Task<HashSet<long>> GetGlobalDynamicWordKeysForWordIds(int? minFreq, int? maxFreq, string? posFilter, List<int> wordIds,
        bool excludeKana = false, FrequencyScope scope = default);
    Task<(int Count, bool WasTruncated)> CountGlobalDynamicWords(int? minFreq, int? maxFreq, string? posFilter, bool excludeKana,
        bool excludeMatureMasteredBlacklisted = false, bool excludeAllTrackedWords = false,
        FrequencyScope scope = default);
    /// <summary>Rank per word key inside the given scope, for the supplied words only; absent = unranked there.</summary>
    Task<Dictionary<(int, byte), int>> GetFrequencyRanks(List<int> wordIds, FrequencyScope scope = default);
    /// <summary>Encoded word key to 1-based rank for a saved list, cached so single-word lookups stay O(1).</summary>
    Task<IReadOnlyDictionary<long, int>> GetListRankMap(long listId);
    Task<(int Count, HashSet<long> WordKeys)> CountDeckWords(DeckWordResolveRequest request, bool excludeKana,
                                                             HashSet<long>? globalFrequencyKeys = null);
    Task<(int Count, HashSet<long> WordKeys)> CountTargetCoverageWords(int deckId, Deck deck, float targetPercentage, bool excludeKana, string? posFilter = null, bool startFromKnown = false);
    Task<(int Count, HashSet<long> WordKeys)> CountStaticDeckWords(int studyDeckId, bool excludeKana,
        bool excludeMatureMasteredBlacklisted = false, bool excludeAllTrackedWords = false);
}
