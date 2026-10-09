using Jiten.Api.Dtos;
using Jiten.Core.Data;
using Jiten.Core.Data.User;

namespace Jiten.Api.Services;

/// <summary>Words a media or media group study deck draws from: one deck as stored, or several decks merged per word.</summary>
public sealed record DeckWordSource
{
    public int? DeckId { get; private init; }
    public IReadOnlyList<int> DeckIds { get; private init; } = [];

    /// <summary>Target-coverage denominator of a single deck; a merged source sums its decks' word counts instead.</summary>
    public int? WordCount { get; private init; }

    /// <summary>The media deck's title or the group's name; null when the caller did not supply it.</summary>
    public string? Title { get; private init; }

    /// <summary>Set for a media group source.</summary>
    public MediaGroupDescription? Group { get; private init; }

    public static DeckWordSource ForDeck(int deckId, int wordCount, string? title = null) =>
        new() { DeckId = deckId, DeckIds = [deckId], WordCount = wordCount, Title = title };

    public static DeckWordSource Merged(IReadOnlyList<int> deckIds, MediaGroupDescription? group = null) =>
        new() { DeckIds = deckIds, Group = group, Title = group?.Titles.OriginalTitle };
}

public record DeckWordResolveRequest(
    DeckWordSource Source,
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
    MediaType? BandSource = null,
    FrequencyScope OrderSource = default)
{
    /// <summary>Saved study decks keep their rank bands global; only the frequency order follows <paramref name="orderSource"/>.</summary>
    public static DeckWordResolveRequest ForStudyDeck(UserStudyDeck sd, DeckWordSource source, FrequencyScope orderSource = default) =>
        new(source, (DeckDownloadType)sd.DownloadType, (DeckOrder)sd.Order, sd.MinFrequency, sd.MaxFrequency, false, false,
            sd.TargetPercentage, sd.MinOccurrences, sd.MaxOccurrences, sd.PosFilter, sd.StartFromKnown, OrderSource: orderSource);

    /// <summary>An explicit download/learn source drives both the rank band and the order.</summary>
    public static FrequencyScope OrderFor(MediaType? frequencySource) => new(frequencySource, null);
}

public class ResolvedWord
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public int Occurrences { get; set; }
    public int SortOrder { get; set; }
}

public record GlobalDynamicResult(List<ResolvedWord> Words, bool WasTruncated);

/// <summary>Frequency-order sort key: rank in the chosen scope, then the global rank for words that scope leaves unranked; unranked sorts last on both.</summary>
public sealed class FrequencyOrderKeys(Dictionary<(int, byte), int> scopedRanks, Dictionary<(int, short), int> globalRanks)
{
    public (int Scoped, int Global) For(int wordId, byte readingIndex)
    {
        var scoped = scopedRanks.TryGetValue((wordId, readingIndex), out var s) && s > 0 ? s : int.MaxValue;
        var global = globalRanks.TryGetValue((wordId, (short)readingIndex), out var g) && g > 0 ? g : int.MaxValue;
        return (scoped, global);
    }
}

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

    /// <summary>Rows of the source; a merged source yields one row per word with DeckWordId set to its release-ordered chrono key, not a real row id.</summary>
    IQueryable<DeckWord> QuerySource(DeckWordSource source);
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
        float? targetPercentage = null, bool startFromKnown = false,
        FrequencyScope orderSource = default);
    Task<HashSet<long>> GetGlobalDynamicWordKeys(int? minFreq, int? maxFreq, string? posFilter,
        FrequencyScope scope = default);
    Task<HashSet<long>> GetGlobalDynamicWordKeysForWordIds(int? minFreq, int? maxFreq, string? posFilter, List<int> wordIds,
        bool excludeKana = false, FrequencyScope scope = default);
    Task<(int Count, bool WasTruncated)> CountGlobalDynamicWords(int? minFreq, int? maxFreq, string? posFilter, bool excludeKana,
        bool excludeMatureMasteredBlacklisted = false, bool excludeAllTrackedWords = false,
        FrequencyScope scope = default);
    /// <summary>Rank per word key inside the given scope, for the supplied words only; absent = unranked there.</summary>
    Task<Dictionary<(int, byte), int>> GetFrequencyRanks(List<int> wordIds, FrequencyScope scope = default);
    Task<FrequencyOrderKeys> LoadFrequencyOrderKeys(List<int> wordIds, FrequencyScope scope);
    /// <summary>Encoded word key to 1-based rank for a saved list, cached so single-word lookups stay O(1).</summary>
    Task<IReadOnlyDictionary<long, int>> GetListRankMap(long listId);
    Task<(int Count, HashSet<long> WordKeys)> CountDeckWords(DeckWordResolveRequest request, bool excludeKana,
                                                             HashSet<long>? globalFrequencyKeys = null);
    Task<(int Count, HashSet<long> WordKeys)> CountTargetCoverageWords(DeckWordSource source, float targetPercentage, bool excludeKana, string? posFilter = null, bool startFromKnown = false);
    Task<(int Count, HashSet<long> WordKeys)> CountStaticDeckWords(int studyDeckId, bool excludeKana,
        bool excludeMatureMasteredBlacklisted = false, bool excludeAllTrackedWords = false);
}
