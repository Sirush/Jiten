using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Billing;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public sealed record RoadmapCandidateSet(
    List<RoadmapCandidate> Candidates,
    Dictionary<int, DeckSummary> Summaries,
    Dictionary<int, int[]> Prerequisites,
    HashSet<int> CompletedDeckIds,
    List<float[]> SeedVectors,
    RoadmapCandidate? Goal);

/// <summary>Builder estimate from precomputed per-deck coverage, not the live known-word walk a run does.</summary>
public sealed record RoadmapPreview(
    int MatchingFilters,
    int Candidates,
    int AboveFloor,
    int AboveComfort,
    bool HasCoverageData,
    double? GoalCoverage);

public sealed record DeckSummary(
    int DeckId,
    string Title,
    string? RomajiTitle,
    string? EnglishTitle,
    string? CoverName,
    int MediaType,
    List<int> Genres,
    double Difficulty,
    long WordCount,
    int CharacterCount,
    long SpeechDuration);

public interface IRoadmapDataLoader
{
    Task<HashSet<long>> LoadKnownWordsAsync(string userId, bool includeLearningWords, CancellationToken ct = default);

    Task<RoadmapCandidateSet> LoadCandidatesAsync(string userId, RoadmapDefinition definition, int? goalDeckId,
                                                  int maxCandidates, CancellationToken ct = default);

    Task<RoadmapPreview> PreviewAsync(string userId, RoadmapDefinition definition, int maxCandidates,
                                      int? goalDeckId = null, CancellationToken ct = default);

    Task<Dictionary<long, int>> LoadFrequencyRanksAsync(IReadOnlyCollection<long> wordKeys, CancellationToken ct = default);

    Task<Dictionary<long, (string Text, string Reading)>> LoadWordTextsAsync(IReadOnlyCollection<long> wordKeys,
                                                                            CancellationToken ct = default);

    Task<(double? ShowsMin, double? ShowsMax, double? NovelsMin, double? NovelsMax)> SuggestDifficultyBandsAsync(
        string userId, CancellationToken ct = default);
}

/// <summary>Loads everything <see cref="RoadmapEngine"/> needs, once per run; separate so the search stays unit-testable.</summary>
public class RoadmapDataLoader(
    IDbContextFactory<JitenDbContext> jitenFactory,
    IDbContextFactory<UserDbContext> userFactory,
    DeckVectorService vectorService,
    ILogger<RoadmapDataLoader> logger) : IRoadmapDataLoader
{
    /// <summary>Stub decks' tiny denominators make the gap walk report absurdly cheap unlocks; excludes ~1% of parent decks.</summary>
    public const int MinDeckWordCount = 2000;

    /// <summary>Media types scored by the novels difficulty model; matches <c>DifficultyComputationJob.GetApiMediaType</c>.</summary>
    private static readonly MediaType[] NovelFamilyTypes =
    {
        MediaType.Novel, MediaType.NonFiction, MediaType.VideoGame,
        MediaType.VisualNovel, MediaType.Manga, MediaType.WebNovel
    };

    /// <summary>Matches <c>DifficultyComputationJob.GetApiMediaType</c>; comfort bands are derived per family.</summary>
    public static DifficultyFamily FamilyOf(MediaType mediaType) =>
        NovelFamilyTypes.Contains(mediaType) ? DifficultyFamily.Novels : DifficultyFamily.Shows;

    /// <summary>Must mirror <c>CoverageComputeService.CreateKnownWordsTempTablesAsync</c> (mature + optional young, kana-expanded, word sets) so roadmap figures match deck-page coverage; LINQ because it must also run on SQLite.</summary>
    public async Task<HashSet<long>> LoadKnownWordsAsync(string userId, bool includeLearningWords,
                                                         CancellationToken ct = default)
    {
        await using var userContext = await userFactory.CreateDbContextAsync(ct);
        await using var jiten = await jitenFactory.CreateDbContextAsync(ct);

        // Blacklisted/mastered/suspended are known words taken out of rotation, not unknowns.
        var matureStates = new HashSet<FsrsState> { FsrsState.Blacklisted, FsrsState.Mastered, FsrsState.Suspended };
        var matureInterval = TimeSpan.FromDays(21);

        var cards = await userContext.FsrsCards.AsNoTracking()
                                     .Where(c => c.UserId == userId)
                                     .Select(c => new { c.WordId, c.ReadingIndex, c.State, c.Due, c.LastReview })
                                     .ToListAsync(ct);

        bool IsMature(FsrsState state, DateTime due, DateTime? lastReview) =>
            matureStates.Contains(state) || (lastReview != null && due - lastReview.Value >= matureInterval);

        // Mirrors the coverage service's _fsrs_young: a completed review is required so brand-new cards don't count.
        var youngStates = new HashSet<FsrsState>
        {
            FsrsState.Learning, FsrsState.Review, FsrsState.Relearning
        };

        var direct = cards
                     .Where(c => IsMature(c.State, c.Due, c.LastReview)
                                 || (includeLearningWords
                                     && youngStates.Contains(c.State)
                                     && c.LastReview != null
                                     && c.Due - c.LastReview.Value < matureInterval))
                     .Select(c => (WordId: c.WordId, ReadingIndex: (int)c.ReadingIndex))
                     .ToList();

        var known = new HashSet<long>(direct.Count * 2);
        foreach (var (wordId, readingIndex) in direct)
            known.Add(RoadmapEngine.PackKey(wordId, readingIndex));

        // Knowing a kanji form implies knowing its kana reading, matching the coverage service's expansion.
        var kanjiFormWordIds = direct.Select(d => d.WordId).Distinct().ToList();
        if (kanjiFormWordIds.Count > 0)
        {
            var forms = await jiten.WordForms.AsNoTracking()
                                   .Where(f => kanjiFormWordIds.Contains(f.WordId))
                                   .Select(f => new { f.WordId, f.ReadingIndex, f.FormType })
                                   .ToListAsync(ct);

            var kanjiForms = forms.Where(f => f.FormType == JmDictFormType.KanjiForm)
                                  .Select(f => (f.WordId, ReadingIndex: (int)f.ReadingIndex))
                                  .ToHashSet();

            var hasMatureKanji = direct.Where(d => kanjiForms.Contains((d.WordId, d.ReadingIndex)))
                                       .Select(d => d.WordId)
                                       .ToHashSet();

            foreach (var form in forms)
            {
                if (form.FormType == JmDictFormType.KanaForm && hasMatureKanji.Contains(form.WordId))
                    known.Add(RoadmapEngine.PackKey(form.WordId, form.ReadingIndex));
            }
        }

        var setIds = await userContext.UserWordSetStates.AsNoTracking()
                                      .Where(s => s.UserId == userId)
                                      .Select(s => s.SetId)
                                      .ToListAsync(ct);

        if (setIds.Count > 0)
        {
            var members = await jiten.WordSetMembers.AsNoTracking()
                                     .Where(m => setIds.Contains(m.SetId))
                                     .Select(m => new { m.WordId, m.ReadingIndex })
                                     .ToListAsync(ct);

            // Mirrors the coverage service's NOT EXISTS: a carded set member counts by card maturity, keeping deck-page parity.
            var cardedKeys = new HashSet<long>(cards.Count);
            foreach (var c in cards)
                cardedKeys.Add(RoadmapEngine.PackKey(c.WordId, (int)c.ReadingIndex));

            foreach (var member in members)
            {
                var key = RoadmapEngine.PackKey(member.WordId, member.ReadingIndex);
                if (!cardedKeys.Contains(key))
                    known.Add(key);
            }
        }

        return known;
    }

    public async Task<RoadmapCandidateSet> LoadCandidatesAsync(string userId, RoadmapDefinition definition,
                                                               int? goalDeckId, int maxCandidates,
                                                               CancellationToken ct = default)
    {
        await using var jiten = await jitenFactory.CreateDbContextAsync(ct);
        await using var userContext = await userFactory.CreateDbContextAsync(ct);

        var preferences = await userContext.UserDeckPreferences.AsNoTracking()
                                           .Where(p => p.UserId == userId)
                                           .Select(p => new { p.DeckId, p.Status, p.IsIgnored })
                                           .ToListAsync(ct);

        var completed = preferences.Where(p => p.Status == DeckStatus.Completed).Select(p => p.DeckId).ToHashSet();
        var ongoing = preferences.Where(p => p.Status == DeckStatus.Ongoing).Select(p => p.DeckId).ToHashSet();
        var dropped = preferences.Where(p => p.Status == DeckStatus.Dropped).Select(p => p.DeckId).ToHashSet();
        var ignored = preferences.Where(p => p.IsIgnored).Select(p => p.DeckId).ToHashSet();
        var planning = preferences.Where(p => p.Status == DeckStatus.Planning).Select(p => p.DeckId).ToHashSet();

        // Ongoing titles steer taste like completed ones, but neither is ever suggested.
        var tasteSeeds = new HashSet<int>(completed);
        tasteSeeds.UnionWith(ongoing);

        var deckIds = await ResolveCandidateIdsAsync(jiten, definition, planning, tasteSeeds, maxCandidates, ct);

        deckIds.ExceptWith(completed);
        deckIds.ExceptWith(ongoing);
        deckIds.ExceptWith(dropped);
        deckIds.ExceptWith(ignored);

        if (goalDeckId.HasValue)
            deckIds.Add(goalDeckId.Value);

        // A pin lost to filtering would silently drop steps the user approved.
        foreach (var pinnedId in definition.PinnedDeckIds)
            deckIds.Add(pinnedId);

        var ordered = deckIds.ToList();
        if (ordered.Count > maxCandidates)
        {
            // Trim within the difficulty bands; an arbitrary cut drops relevant decks as often as not.
            ordered = await TrimToBandAsync(jiten, ordered, definition, goalDeckId, maxCandidates, ct);
        }

        var summaries = await jiten.Decks.AsNoTracking()
                                  .Where(d => ordered.Contains(d.DeckId))
                                  .Select(d => new DeckSummary(
                                              d.DeckId,
                                              d.OriginalTitle,
                                              d.RomajiTitle,
                                              d.EnglishTitle,
                                              d.CoverName,
                                              (int)d.MediaType,
                                              d.DeckGenres.Select(g => (int)g.Genre).ToList(),
                                              (d.DifficultyOverride > -1 ? d.DifficultyOverride : d.Difficulty)
                                              + (float)(d.DeckDifficulty != null ? d.DeckDifficulty.UserAdjustment : 0),
                                              d.WordCount,
                                              d.CharacterCount,
                                              d.SpeechDuration))
                                  .ToDictionaryAsync(d => d.DeckId, ct);

        var words = await LoadDeckWordsAsync(jiten, ordered, ct);

        var candidates = new List<RoadmapCandidate>(ordered.Count);
        RoadmapCandidate? goal = null;

        foreach (var deckId in ordered)
        {
            if (!summaries.TryGetValue(deckId, out var summary) || summary.WordCount <= 0)
                continue;
            if (!words.TryGetValue(deckId, out var deckWords) || deckWords.Length == 0)
                continue;

            vectorService.TryGetVector(deckId, out var vector);

            var candidate = new RoadmapCandidate
            {
                DeckId = deckId,
                WordCount = summary.WordCount,
                Words = deckWords,
                Vector = vector
            };

            if (goalDeckId.HasValue && deckId == goalDeckId.Value)
                goal = candidate;
            else
                candidates.Add(candidate);
        }

        var prerequisites = await LoadPrerequisitesAsync(jiten, ordered, ct);
        var seedVectors = LoadSeedVectors(tasteSeeds);

        logger.LogInformation("Roadmap: user {UserId} candidate set = {Count} decks ({Seeds} taste seeds, goal={Goal})",
                              userId, candidates.Count, tasteSeeds.Count, goalDeckId);

        return new RoadmapCandidateSet(candidates, summaries, prerequisites, completed, seedVectors, goal);
    }

    /// <summary>Counts what a definition would offer so the builder can warn before a near-empty generation; chunk-based coverage, so counts can drift by a title or two.</summary>
    public async Task<RoadmapPreview> PreviewAsync(string userId, RoadmapDefinition definition, int maxCandidates,
                                                    int? goalDeckId = null, CancellationToken ct = default)
    {
        await using var jiten = await jitenFactory.CreateDbContextAsync(ct);
        await using var userContext = await userFactory.CreateDbContextAsync(ct);

        var preferences = await userContext.UserDeckPreferences.AsNoTracking()
                                           .Where(p => p.UserId == userId)
                                           .Select(p => new { p.DeckId, p.Status, p.IsIgnored })
                                           .ToListAsync(ct);

        var completed = preferences.Where(p => p.Status == DeckStatus.Completed).Select(p => p.DeckId).ToHashSet();
        var ongoing = preferences.Where(p => p.Status == DeckStatus.Ongoing).Select(p => p.DeckId).ToHashSet();
        var dropped = preferences.Where(p => p.Status == DeckStatus.Dropped).Select(p => p.DeckId).ToHashSet();
        var ignored = preferences.Where(p => p.IsIgnored).Select(p => p.DeckId).ToHashSet();
        var planning = preferences.Where(p => p.Status == DeckStatus.Planning).Select(p => p.DeckId).ToHashSet();

        var tasteSeeds = new HashSet<int>(completed);
        tasteSeeds.UnionWith(ongoing);

        var unavailable = new HashSet<int>(tasteSeeds);
        unavailable.UnionWith(dropped);
        unavailable.UnionWith(ignored);

        var filtered = await ResolveFilteredIdsAsync(jiten, definition, ct);
        var matchingFilters = filtered.Count(id => !unavailable.Contains(id));

        // ApplySeeding may hand back the set it was given, so nothing may be read out of `filtered` after this.
        var candidates = ApplySeeding(filtered, definition, planning, tasteSeeds, maxCandidates);
        candidates.ExceptWith(unavailable);

        var candidateIds = candidates.ToList();

        // Never-computed coverage reads as 0% everywhere — that's "unknown", not "nothing readable".
        var hasCoverageData = await userContext.UserCoverageChunks.AsNoTracking()
                                               .AnyAsync(c => c.UserId == userId, ct);

        var aboveFloor = 0;
        var aboveComfort = 0;
        double? goalCoverage = null;

        if (hasCoverageData && (candidateIds.Count > 0 || goalDeckId.HasValue))
        {
            var lookupIds = new List<int>(candidateIds);
            if (goalDeckId.HasValue && !candidates.Contains(goalDeckId.Value))
                lookupIds.Add(goalDeckId.Value);

            var coverage = await UserCoverageChunkHelper.GetCoverage(userContext, userId, lookupIds);

            // Chunk coverage is a percentage; the definition's thresholds are fractions.
            var floor = definition.ComprehensionFloor * 100;
            var comfort = definition.ComfortTarget * 100;

            double TotalFor(int deckId)
            {
                var total = (double)coverage.MatureCoverage.GetValueOrDefault(deckId);
                return definition.IncludeLearningWords
                    ? Math.Min(total + coverage.YoungCoverage.GetValueOrDefault(deckId), 100)
                    : total;
            }

            foreach (var deckId in candidateIds)
            {
                var total = TotalFor(deckId);
                if (total >= floor) aboveFloor++;
                if (total >= comfort) aboveComfort++;
            }

            // Reported as a fraction so it lines up with the generated plan's goal coverage figures.
            if (goalDeckId.HasValue)
                goalCoverage = TotalFor(goalDeckId.Value) / 100;
        }

        return new RoadmapPreview(matchingFilters, candidateIds.Count, aboveFloor, aboveComfort, hasCoverageData,
                                  goalCoverage);
    }

    private async Task<HashSet<int>> ResolveCandidateIdsAsync(JitenDbContext jiten, RoadmapDefinition definition,
                                                              HashSet<int> planning, HashSet<int> tasteSeeds,
                                                              int maxCandidates, CancellationToken ct)
    {
        var filtered = await ResolveFilteredIdsAsync(jiten, definition, ct);
        return ApplySeeding(filtered, definition, planning, tasteSeeds, maxCandidates);
    }

    private static async Task<HashSet<int>> ResolveFilteredIdsAsync(JitenDbContext jiten, RoadmapDefinition definition,
                                                                    CancellationToken ct)
    {
        var filter = ToFilterDefinition(definition);
        var query = DeckFilterHelper.BuildQuery(jiten, filter, FrequencyListMode.Filters)
                                    .Where(d => d.WordCount >= MinDeckWordCount);

        // Bands are per model family, so they can't use the filter helper's single min/max pair.
        query = ApplyDifficultyBands(query, definition);

        query = ApplyAdultFilter(query, definition);

        var filtered = await query.Select(d => d.DeckId).ToListAsync(ct);
        return filtered.ToHashSet();
    }

    /// <summary>Narrows to the seeded set; may return <paramref name="filteredSet"/> itself, so callers don't own their argument.</summary>
    private HashSet<int> ApplySeeding(HashSet<int> filteredSet, RoadmapDefinition definition,
                                      HashSet<int> planning, HashSet<int> tasteSeeds, int maxCandidates)
    {
        if (definition.CandidateMode == RoadmapCandidateMode.CatalogWide)
            return filteredSet;

        // Seeds are intersected with the filters so preferences never override an explicit filter.
        var seeded = new HashSet<int>();

        foreach (var deckId in planning)
        {
            if (filteredSet.Contains(deckId))
                seeded.Add(deckId);
        }

        if (tasteSeeds.Count > 0)
        {
            // Rank within the filtered set: a global top-N ∩ a narrow filter can collapse to a handful of decks.
            var similar = vectorService.FindSimilarToSet(tasteSeeds, maxCandidates, filteredSet);
            foreach (var (deckId, _) in similar)
                seeded.Add(deckId);
        }

        // A user with no history at all would otherwise get an empty roadmap; fall back to the filters.
        return seeded.Count > 0 ? seeded : filteredSet;
    }

    /// <summary>Must match <c>DeckFilterHelper</c>/<c>DifficultyMapper</c>: a band selects the same decks here as in browse.</summary>
    private static IQueryable<Deck> ApplyDifficultyBands(IQueryable<Deck> query, RoadmapDefinition definition)
    {
        var showsMin = (float?)definition.ShowsDifficultyMin;
        var showsMax = (float?)definition.ShowsDifficultyMax;
        var novelsMin = (float?)definition.NovelsDifficultyMin;
        var novelsMax = (float?)definition.NovelsDifficultyMax;

        if (showsMin is null && showsMax is null && novelsMin is null && novelsMax is null)
            return query;

        return query.Where(d =>
            NovelFamilyTypes.Contains(d.MediaType)
                ? (novelsMin == null || (d.DifficultyOverride > -1 ? d.DifficultyOverride : d.Difficulty)
                   + (float)(d.DeckDifficulty != null ? d.DeckDifficulty.UserAdjustment : 0) >= novelsMin)
                  && (novelsMax == null || (d.DifficultyOverride > -1 ? d.DifficultyOverride : d.Difficulty)
                      + (float)(d.DeckDifficulty != null ? d.DeckDifficulty.UserAdjustment : 0) <= novelsMax)
                : (showsMin == null || (d.DifficultyOverride > -1 ? d.DifficultyOverride : d.Difficulty)
                   + (float)(d.DeckDifficulty != null ? d.DeckDifficulty.UserAdjustment : 0) >= showsMin)
                  && (showsMax == null || (d.DifficultyOverride > -1 ? d.DifficultyOverride : d.Difficulty)
                      + (float)(d.DeckDifficulty != null ? d.DeckDifficulty.UserAdjustment : 0) <= showsMax));
    }

    private static IQueryable<Deck> ApplyAdultFilter(IQueryable<Deck> query, RoadmapDefinition definition)
    {
        if (!definition.IncludeAdultOnly)
            return query.Where(d => !d.DeckGenres.Any(dg => dg.Genre == Genre.AdultOnly));

        if (definition.AdultOnlyExclusive)
            return query.Where(d => d.DeckGenres.Any(dg => dg.Genre == Genre.AdultOnly));

        return query;
    }

    private static FrequencyListDefinition ToFilterDefinition(RoadmapDefinition definition) => new()
    {
        MediaTypes = definition.MediaTypes,
        YearFrom = definition.YearFrom,
        YearTo = definition.YearTo,
        GenresInclude = definition.GenresInclude,
        GenresExclude = definition.GenresExclude,
        TagsInclude = definition.TagsInclude,
        TagsExclude = definition.TagsExclude
    };

    private static async Task<List<int>> TrimToBandAsync(JitenDbContext jiten, List<int> deckIds,
                                                         RoadmapDefinition definition, int? goalDeckId,
                                                         int maxCandidates, CancellationToken ct)
    {
        var showsCentre = Midpoint(definition.ShowsDifficultyMin, definition.ShowsDifficultyMax);
        var novelsCentre = Midpoint(definition.NovelsDifficultyMin, definition.NovelsDifficultyMax);

        var rows = await jiten.Decks.AsNoTracking()
                              .Where(d => deckIds.Contains(d.DeckId))
                              .Select(d => new
                              {
                                  d.DeckId,
                                  d.MediaType,
                                  Difficulty = (d.DifficultyOverride > -1 ? d.DifficultyOverride : d.Difficulty)
                                               + (float)(d.DeckDifficulty != null ? d.DeckDifficulty.UserAdjustment : 0)
                              })
                              .ToListAsync(ct);

        List<int> trimmed;
        if (rows.Count <= maxCandidates)
        {
            trimmed = rows.Select(r => r.DeckId).ToList();
        }
        else
        {
            var shows = rows.Where(r => FamilyOf(r.MediaType) == DifficultyFamily.Shows)
                            .Select(r => (r.DeckId, Difficulty: (double)r.Difficulty)).ToList();
            var novels = rows.Where(r => FamilyOf(r.MediaType) == DifficultyFamily.Novels)
                             .Select(r => (r.DeckId, Difficulty: (double)r.Difficulty)).ToList();

            // Slots split proportionally so the trim cannot erase the smaller family outright.
            var showsTake = (int)Math.Round((double)maxCandidates * shows.Count / rows.Count);
            showsTake = Math.Clamp(showsTake, maxCandidates - novels.Count, Math.Min(shows.Count, maxCandidates));

            trimmed = PickWithinFamily(shows, showsCentre, showsTake);
            trimmed.AddRange(PickWithinFamily(novels, novelsCentre, maxCandidates - showsTake));
        }

        if (goalDeckId.HasValue && !trimmed.Contains(goalDeckId.Value))
            trimmed.Add(goalDeckId.Value);

        // The accepted prefix must survive the trim; see LoadCandidatesAsync.
        var present = deckIds.ToHashSet();
        foreach (var pinnedId in definition.PinnedDeckIds)
        {
            if (present.Contains(pinnedId) && !trimmed.Contains(pinnedId))
                trimmed.Add(pinnedId);
        }

        return trimmed;
    }

    /// <summary>Keeps <paramref name="take"/> decks nearest the band centre; with no band, an even spread — a fixed centre would trim away the easy titles beginners need.</summary>
    private static List<int> PickWithinFamily(List<(int DeckId, double Difficulty)> rows, double? centre, int take)
    {
        if (take <= 0)
            return new List<int>();
        if (rows.Count <= take)
            return rows.Select(r => r.DeckId).ToList();

        if (centre.HasValue)
            return rows.OrderBy(r => Math.Abs(r.Difficulty - centre.Value)).Take(take).Select(r => r.DeckId).ToList();

        var sorted = rows.OrderBy(r => r.Difficulty).ToList();
        var picked = new List<int>(take);
        for (var i = 0; i < take; i++)
            picked.Add(sorted[(int)((long)i * sorted.Count / take)].DeckId);
        return picked;
    }

    private static double? Midpoint(double? min, double? max)
    {
        if (min.HasValue && max.HasValue) return (min.Value + max.Value) / 2;
        return min ?? max;
    }

    private static async Task<Dictionary<int, RoadmapWord[]>> LoadDeckWordsAsync(JitenDbContext jiten,
                                                                                 List<int> deckIds,
                                                                                 CancellationToken ct)
    {
        // Descending occurrences is a contract of RoadmapCandidate.Words; consumers don't re-sort.
        // Streamed: materialising ~10M rows as projection objects costs hundreds of MB the arrays don't.
        var rows = jiten.DeckWords.AsNoTracking()
                        .Where(dw => deckIds.Contains(dw.DeckId))
                        .OrderBy(dw => dw.DeckId)
                        .ThenByDescending(dw => dw.Occurrences)
                        .Select(dw => new { dw.DeckId, dw.WordId, dw.ReadingIndex, dw.Occurrences })
                        .AsAsyncEnumerable();

        var result = new Dictionary<int, RoadmapWord[]>(deckIds.Count);
        var current = new List<RoadmapWord>();
        var currentDeckId = -1;

        await foreach (var row in rows.WithCancellation(ct))
        {
            if (row.DeckId != currentDeckId)
            {
                if (currentDeckId >= 0)
                    result[currentDeckId] = current.ToArray();
                current = new List<RoadmapWord>();
                currentDeckId = row.DeckId;
            }

            current.Add(new RoadmapWord(RoadmapEngine.PackKey(row.WordId, row.ReadingIndex), row.Occurrences));
        }

        if (currentDeckId >= 0)
            result[currentDeckId] = current.ToArray();

        return result;
    }

    /// <summary>Story-continuity only — a sequel never precedes its prequel; the objective already anti-selects sequels.</summary>
    private static async Task<Dictionary<int, int[]>> LoadPrerequisitesAsync(JitenDbContext jiten, List<int> deckIds,
                                                                             CancellationToken ct)
    {
        // Only primary rows are persisted (no Prequel rows); `source --Sequel--> target` makes the target the prerequisite.
        var relationships = await jiten.DeckRelationships.AsNoTracking()
                                       .Where(r => deckIds.Contains(r.SourceDeckId)
                                                   && r.RelationshipType == DeckRelationshipType.Sequel)
                                       .Select(r => new { r.SourceDeckId, r.TargetDeckId })
                                       .ToListAsync(ct);

        return relationships
               .GroupBy(r => r.SourceDeckId)
               .ToDictionary(g => g.Key, g => g.Select(r => r.TargetDeckId).Distinct().ToArray());
    }

    private List<float[]> LoadSeedVectors(HashSet<int> tasteSeedDeckIds)
    {
        var vectors = new List<float[]>();
        foreach (var deckId in tasteSeedDeckIds)
        {
            if (vectorService.TryGetVector(deckId, out var vector))
                vectors.Add(vector);
        }

        return vectors;
    }

    public async Task<Dictionary<long, int>> LoadFrequencyRanksAsync(IReadOnlyCollection<long> wordKeys,
                                                                     CancellationToken ct = default)
    {
        if (wordKeys.Count == 0)
            return new Dictionary<long, int>();

        await using var jiten = await jitenFactory.CreateDbContextAsync(ct);

        var wordIds = wordKeys.Select(RoadmapEngine.UnpackWordId).Distinct().ToArray();

        // Filtering by WordId (a pair-wise IN would not translate) returns every reading; keep only the requested forms.
        var wanted = wordKeys as HashSet<long> ?? wordKeys.ToHashSet();

        var rows = jiten.WordFormFrequencies.AsNoTracking()
                        .Where(f => wordIds.Contains(f.WordId))
                        .Select(f => new { f.WordId, f.ReadingIndex, f.FrequencyRank })
                        .AsAsyncEnumerable();

        var ranks = new Dictionary<long, int>(wanted.Count);
        await foreach (var row in rows.WithCancellation(ct))
        {
            var key = RoadmapEngine.PackKey(row.WordId, row.ReadingIndex);
            if (wanted.Contains(key))
                ranks[key] = row.FrequencyRank;
        }

        return ranks;
    }

    public async Task<Dictionary<long, (string Text, string Reading)>> LoadWordTextsAsync(
        IReadOnlyCollection<long> wordKeys, CancellationToken ct = default)
    {
        var result = new Dictionary<long, (string, string)>();
        if (wordKeys.Count == 0)
            return result;

        await using var jiten = await jitenFactory.CreateDbContextAsync(ct);

        var wordIds = wordKeys.Select(RoadmapEngine.UnpackWordId).Distinct().ToArray();

        var forms = await jiten.WordForms.AsNoTracking()
                               .Where(f => wordIds.Contains(f.WordId))
                               .Select(f => new { f.WordId, f.ReadingIndex, f.Text, f.FormType })
                               .ToListAsync(ct);

        var byWord = forms.GroupBy(f => f.WordId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var key in wordKeys)
        {
            var wordId = RoadmapEngine.UnpackWordId(key);
            var readingIndex = RoadmapEngine.UnpackReadingIndex(key);

            if (!byWord.TryGetValue(wordId, out var wordForms) || wordForms.Count == 0)
                continue;

            var form = wordForms.FirstOrDefault(f => f.ReadingIndex == readingIndex) ?? wordForms[0];
            var kana = wordForms.FirstOrDefault(f => f.FormType == JmDictFormType.KanaForm);

            result[key] = (form.Text, kana?.Text ?? form.Text);
        }

        return result;
    }

    /// <summary>Suggests a band per model family from completed titles; comfort in audio-visual media doesn't transfer to text.</summary>
    public async Task<(double? ShowsMin, double? ShowsMax, double? NovelsMin, double? NovelsMax)>
        SuggestDifficultyBandsAsync(string userId, CancellationToken ct = default)
    {
        await using var userContext = await userFactory.CreateDbContextAsync(ct);
        await using var jiten = await jitenFactory.CreateDbContextAsync(ct);

        var completedIds = await userContext.UserDeckPreferences.AsNoTracking()
                                            .Where(p => p.UserId == userId && p.Status == DeckStatus.Completed)
                                            .Select(p => p.DeckId)
                                            .ToListAsync(ct);

        if (completedIds.Count == 0)
            return (null, null, null, null);

        var rows = await jiten.Decks.AsNoTracking()
                              .Where(d => completedIds.Contains(d.DeckId))
                              .Select(d => new
                              {
                                  d.MediaType,
                                  Difficulty = (d.DifficultyOverride > -1 ? d.DifficultyOverride : d.Difficulty)
                                               + (float)(d.DeckDifficulty != null ? d.DeckDifficulty.UserAdjustment : 0)
                              })
                              .ToListAsync(ct);

        var shows = rows.Where(r => FamilyOf(r.MediaType) == DifficultyFamily.Shows).Select(r => (double)r.Difficulty).ToList();
        var novels = rows.Where(r => FamilyOf(r.MediaType) == DifficultyFamily.Novels).Select(r => (double)r.Difficulty).ToList();

        var (showsMin, showsMax) = BandFrom(shows);
        var (novelsMin, novelsMax) = BandFrom(novels);

        return (showsMin, showsMax, novelsMin, novelsMax);
    }

    private static (double? Min, double? Max) BandFrom(List<double> difficulties)
    {
        if (difficulties.Count == 0)
            return (null, null);

        difficulties.Sort();
        var median = difficulties[difficulties.Count / 2];

        // Reach slightly above what they have finished — a roadmap that never stretches is not a roadmap.
        var min = Math.Max(0, median - 0.75);
        var max = Math.Min(5, median + 1.0);

        // Snap outward to the frontend slider's 0.1 steps so rounding can only widen the band, never narrow it.
        return (Math.Floor(min * 10) / 10, Math.Ceiling(max * 10) / 10);
    }
}
