using System.Diagnostics;
using System.Text.Json;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Jiten.Core.Services.SmartDeck;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using NpgsqlTypes;
using StackExchange.Redis;

namespace Jiten.Api.Services.SmartDeck;

public sealed record SmartDeckSources(
    SmartDeckSettings Settings,
    IReadOnlyList<SmartDeckTitle> Titles,
    IReadOnlyDictionary<int, SmartDeckUnitWindow> Windows,
    int TitlesBeyondCap,
    IReadOnlySet<int>? TitlesWithUnits = null)
{
    public IReadOnlySet<int> TitlesWithUnits { get; init; } = TitlesWithUnits ?? new HashSet<int>();

    public bool ContributesWholeTitle(int parentDeckId) => Settings.RestTargetPercentage > 0 || !TitlesWithUnits.Contains(parentDeckId);
}

public sealed record SmartDeckBuildResult(bool Built, string? SkipReason, int TitleCount, int WordCount, TimeSpan Duration);

public sealed record SmartDeckPreviewCount(int Words, int NewWords, int WindowWords);
public sealed record SmartDeckWordFilters(bool ExcludeKana, int? MinGlobalFrequency, int? MaxGlobalFrequency, string? PosFilter)
{
    public static readonly SmartDeckWordFilters None = new(false, null, null, null);

    public static SmartDeckWordFilters From(UserStudyDeck deck)
        => new(deck.ExcludeKana, deck.MinGlobalFrequency, deck.MaxGlobalFrequency, deck.PosFilter);
}

public sealed record SmartDeckContribution(int ParentDeckId, int Occurrences, int? WindowUnitDeckId, int WindowOccurrences, double Share);

public interface ISmartDeckBuilder
{
    Task<SmartDeckSources> LoadSources(string userId, SmartDeckSettings settings, CancellationToken ct = default);
    Task<SmartDeckSources> LoadSourcesCached(string userId, CancellationToken ct = default);

    void ForgetSources(string userId);

    Task<List<SmartDeckScoredWord>> Score(string userId, SmartDeckSources sources, SmartDeckWordFilters filters, int cap, CancellationToken ct = default);
    Task<SmartDeckPreviewCount> CountPreview(string userId, SmartDeckSources sources, SmartDeckWordFilters filters, CancellationToken ct = default);

    Task<Dictionary<long, List<SmartDeckContribution>>> Explain(SmartDeckSources sources, IReadOnlyCollection<long> keys, CancellationToken ct = default);

    Task<SmartDeckBuildResult> Rebuild(string userId, CancellationToken ct = default);
    Task<DateTime?> GetLastRebuilt(string userId);
    Task ClearLastRebuilt(string userId);
}

public static class SmartDeckCacheKeys
{
    public static string Sources(string userId) => $"smartdeck:sources:{userId}";
    public static string LastRebuilt(string userId) => $"smartdeck:rebuilt:{userId}";
    public static string UnitReport(string userId, int deckId) => $"smartdeck:unit-report:{userId}:{deckId}";
}

public class SmartDeckBuilder(
    JitenDbContext context,
    UserDbContext userContext,
    IWordReferenceCache referenceCache,
    IDerivationLinkCache derivationCache,
    IWordFormSiblingCache siblingCache,
    IMemoryCache memoryCache,
    IStudyDeckMembershipService deckMembership,
    IStudySessionService sessionService,
    IUserLimitsService userLimits,
    IConnectionMultiplexer redis,
    ILogger<SmartDeckBuilder> logger) : ISmartDeckBuilder
{
    private const int PosFilterChunk = 20_000;
    private static readonly TimeSpan SourcesCacheTtl = TimeSpan.FromMinutes(5);

    public async Task<SmartDeckSources> LoadSources(string userId, SmartDeckSettings settings, CancellationToken ct = default)
    {
        var preferences = await userContext.UserDeckPreferences.AsNoTracking()
                                           .Where(p => p.UserId == userId && p.Status != DeckStatus.None)
                                           .Select(p => new { p.DeckId, p.Status, p.UpdatedAt })
                                           .ToListAsync(ct);

        var referencedIds = preferences.Select(p => p.DeckId)
                                       .Concat(settings.PinnedDeckIds)
                                       .Concat(settings.IncludedDeckIds)
                                       .Distinct()
                                       .ToList();
        var hierarchy = await context.Decks.AsNoTracking()
                                     .Where(d => referencedIds.Contains(d.DeckId))
                                     .Select(d => new { d.DeckId, d.ParentDeckId })
                                     .ToDictionaryAsync(d => d.DeckId, d => d.ParentDeckId, ct);

        var validParents = new HashSet<int>(hierarchy.Where(kv => kv.Value == null).Select(kv => kv.Key));
        var effective = settings with
        {
            PinnedDeckIds = settings.PinnedDeckIds.Where(validParents.Contains).ToList(),
            IncludedDeckIds = settings.IncludedDeckIds.Where(validParents.Contains).ToList(),
        };

        var rows = preferences.Where(p => hierarchy.ContainsKey(p.DeckId))
                              .Select(p => new SmartDeckPreferenceRow(p.DeckId, hierarchy[p.DeckId], p.Status, p.UpdatedAt))
                              .ToList();

        var now = DateTime.UtcNow;
        var titles = SmartDeckSourceResolver.ResolveTitles(effective, rows, now);
        var eligibleCount = CountEligible(effective, rows);

        var titleIds = titles.Select(t => t.ParentDeckId).ToList();
        var boostedIds = titles.Where(t => t.Boosted).Select(t => t.ParentDeckId).ToList();
        var windows = new Dictionary<int, SmartDeckUnitWindow>();
        var titlesWithUnits = new HashSet<int>();
        if (titleIds.Count > 0)
        {
            var allUnits = await context.Decks.AsNoTracking()
                                        .Where(d => d.ParentDeckId != null && titleIds.Contains(d.ParentDeckId.Value))
                                        .Select(d => new { d.DeckId, ParentDeckId = d.ParentDeckId!.Value, d.DeckOrder })
                                        .ToListAsync(ct);
            foreach (var u in allUnits) titlesWithUnits.Add(u.ParentDeckId);
            var units = allUnits.Where(u => boostedIds.Contains(u.ParentDeckId)).ToList();
            var mediaTypes = await context.Decks.AsNoTracking()
                                          .Where(d => boostedIds.Contains(d.DeckId))
                                          .Select(d => new { d.DeckId, d.MediaType })
                                          .ToDictionaryAsync(d => d.DeckId, d => d.MediaType, ct);
            var completed = preferences.Where(p => p.Status == DeckStatus.Completed).Select(p => p.DeckId).ToHashSet();
            var ongoing = preferences.Where(p => p.Status == DeckStatus.Ongoing).Select(p => p.DeckId).ToHashSet();

            foreach (var group in units.GroupBy(u => u.ParentDeckId))
            {
                var mediaType = mediaTypes.GetValueOrDefault(group.Key);
                var sequential = effective.SequenceOverrides.TryGetValue(group.Key, out var forced)
                    ? forced
                    : SmartDeckConstants.IsSequentialByDefault(mediaType);
                windows[group.Key] = SmartDeckSourceResolver.ResolveWindow(
                    group.Key, group.Select(u => (u.DeckId, u.DeckOrder)).ToList(), completed, effective.LookaheadFor(mediaType), ongoing, sequential);
            }
        }

        return new SmartDeckSources(effective, titles, windows, Math.Max(0, eligibleCount - titles.Count), titlesWithUnits);
    }

    public async Task<SmartDeckSources> LoadSourcesCached(string userId, CancellationToken ct = default)
    {
        if (memoryCache.TryGetValue(SmartDeckCacheKeys.Sources(userId), out SmartDeckSources? cached) && cached != null) return cached;

        var settingsJson = await userContext.UserSettings.AsNoTracking()
                                            .Where(us => us.UserId == userId)
                                            .Select(us => us.SmartDeckJson)
                                            .FirstOrDefaultAsync(ct);
        var sources = await LoadSources(userId, SmartDeckSettings.Parse(settingsJson), ct);
        memoryCache.Set(SmartDeckCacheKeys.Sources(userId), sources, SourcesCacheTtl);
        return sources;
    }

    public void ForgetSources(string userId) => memoryCache.Remove(SmartDeckCacheKeys.Sources(userId));

    public async Task<SmartDeckBuildResult> Rebuild(string userId, CancellationToken ct = default)
    {
        ForgetSources(userId);
        var sw = Stopwatch.StartNew();

        var deck = await userContext.UserStudyDecks
                                    .FirstOrDefaultAsync(sd => sd.UserId == userId && sd.DeckType == StudyDeckType.Smart, ct);
        if (deck == null) return new SmartDeckBuildResult(false, "no smart deck", 0, 0, sw.Elapsed);

        var settingsJson = await userContext.UserSettings.AsNoTracking()
                                            .Where(us => us.UserId == userId)
                                            .Select(us => us.SmartDeckJson)
                                            .FirstOrDefaultAsync(ct);
        var settings = SmartDeckSettings.Parse(settingsJson);
        var limits = await userLimits.GetLimitsAsync(userId, ct);

        // Pause is the user's; only a tier lapse forces it, and rows survive so a resubscribe just needs an unpause.
        if (deck.IsActive && !limits.IsPlus)
        {
            deck.IsActive = false;
            await userContext.SaveChangesAsync(ct);
            await sessionService.BumpStudyOverviewVersion(userId);
        }
        if (!settings.Enabled) return new SmartDeckBuildResult(false, "disabled", 0, 0, sw.Elapsed);
        if (!limits.IsPlus) return new SmartDeckBuildResult(false, "not plus", 0, 0, sw.Elapsed);

        var sources = await LoadSources(userId, settings, ct);
        var ranked = await Score(userId, sources, SmartDeckWordFilters.From(deck), SmartDeckConstants.MaxWords, ct);

        await WriteRows(deck.UserStudyDeckId, ranked, ct);

        await deckMembership.Invalidate(userId, deck.UserStudyDeckId);
        await sessionService.BumpStudyOverviewVersion(userId);
        await StampRebuilt(userId);

        logger.LogInformation("Smart deck rebuilt for {UserId}: {Titles} titles, {Words} words in {Ms} ms",
                              userId, sources.Titles.Count, ranked.Count, sw.ElapsedMilliseconds);
        return new SmartDeckBuildResult(true, null, sources.Titles.Count, ranked.Count, sw.Elapsed);
    }

    public async Task<List<SmartDeckScoredWord>> Score(string userId, SmartDeckSources sources, SmartDeckWordFilters filters, int cap, CancellationToken ct = default)
        => (await ScoreCore(userId, sources, filters, cap, ct)).Ranked;

    private async Task<(List<SmartDeckScoredWord> Ranked, HashSet<long> CardKeys, Dictionary<int, List<SmartDeckWordOccurrence>> WindowWords)> ScoreCore(
        string userId, SmartDeckSources sources, SmartDeckWordFilters filters, int cap, CancellationToken ct)
    {
        await referenceCache.EnsureLoaded(ct);

        var parentIds = sources.Titles.Select(t => t.ParentDeckId).Where(sources.ContributesWholeTitle).ToList();
        var windowIds = sources.Windows.Values.SelectMany(w => w.WindowDeckIds).Distinct().ToList();
        var parentWords = await LoadWords(parentIds, ct);
        var windowWords = await LoadWords(windowIds, ct);
        var (excludedKeys, cardKeys) = await LoadExclusionKeys(userId, ct);

        var inputs = BuildInputs(sources, parentWords, windowWords);

        var minRank = filters.MinGlobalFrequency is > 0 ? filters.MinGlobalFrequency : null;
        var maxRank = filters.MaxGlobalFrequency is > 0 ? filters.MaxGlobalFrequency : null;
        bool FailsFilters(long key)
        {
            if (filters.ExcludeKana && referenceCache.IsKanaForm(key)) return true;
            if (minRank == null && maxRank == null) return false;
            if (!HasRank(key, out var rank)) return true;
            return (minRank != null && rank < minRank) || (maxRank != null && rank > maxRank);
        }
        bool IsExcluded(long key) => excludedKeys.Contains(key) || FailsFilters(key);
        bool HasCard(long key) => cardKeys.Contains(key) && !FailsFilters(key);
        int Rank(long key) => HasRank(key, out var rank) ? rank : int.MaxValue;

        var needsPosFilter = !string.IsNullOrWhiteSpace(filters.PosFilter);
        var ranked = SmartDeckScorer.Score(inputs, IsExcluded, Rank, needsPosFilter ? int.MaxValue : cap, HasCard);
        if (needsPosFilter) ranked = await ApplyPosFilter(ranked, filters.PosFilter!, cap, ct);
        return (ranked, cardKeys, windowWords);
    }

    public async Task<SmartDeckPreviewCount> CountPreview(string userId, SmartDeckSources sources, SmartDeckWordFilters filters, CancellationToken ct = default)
    {
        var (ranked, cardKeys, windowWords) = await ScoreCore(userId, sources, filters, SmartDeckConstants.MaxWords, ct);
        var fresh = ranked.Where(w => !cardKeys.Contains(w.Key)).ToList();
        if (windowWords.Count == 0 || fresh.Count == 0) return new SmartDeckPreviewCount(ranked.Count, fresh.Count, 0);

        var windowKeys = new HashSet<long>();
        foreach (var words in windowWords.Values)
            foreach (var w in words)
                windowKeys.Add(WordFormHelper.EncodeWordKey(w.WordId, w.ReadingIndex));
        return new SmartDeckPreviewCount(ranked.Count, fresh.Count, fresh.Count(w => windowKeys.Contains(w.Key)));
    }

    public async Task<Dictionary<long, List<SmartDeckContribution>>> Explain(SmartDeckSources sources, IReadOnlyCollection<long> keys, CancellationToken ct = default)
    {
        var result = new Dictionary<long, List<SmartDeckContribution>>();
        if (keys.Count == 0 || sources.Titles.Count == 0) return result;

        var wordIds = keys.Select(k => (int)(k >> 8)).Distinct().ToList();
        var parentIds = sources.Titles.Select(t => t.ParentDeckId).ToList();
        var windowIds = sources.Windows.Values.SelectMany(w => w.WindowDeckIds).Distinct().ToList();
        var deckIds = parentIds.Concat(windowIds).ToList();

        var rows = await context.DeckWords.AsNoTracking()
                                .Where(w => deckIds.Contains(w.DeckId) && wordIds.Contains(w.WordId))
                                .Select(w => new { w.DeckId, w.WordId, w.ReadingIndex, w.Occurrences })
                                .ToListAsync(ct);
        var keySet = keys.ToHashSet();
        var byKeyAndDeck = rows.Where(r => keySet.Contains(WordFormHelper.EncodeWordKey(r.WordId, r.ReadingIndex)))
                               .ToDictionary(r => (WordFormHelper.EncodeWordKey(r.WordId, r.ReadingIndex), r.DeckId), r => r.Occurrences);

        foreach (var key in keySet)
        {
            var contributions = new List<(SmartDeckContribution Item, double Raw)>();
            foreach (var title in sources.Titles)
            {
                var occ = sources.ContributesWholeTitle(title.ParentDeckId) ? byKeyAndDeck.GetValueOrDefault((key, title.ParentDeckId)) : 0;
                var raw = title.Weight * SmartDeckConstants.WholeTitleWeight * Math.Log2(1 + occ);
                int? unitId = null;
                var unitOcc = 0;
                if (title.Boosted && sources.Windows.TryGetValue(title.ParentDeckId, out var window))
                {
                    foreach (var u in window.WindowDeckIds)
                    {
                        var uOcc = byKeyAndDeck.GetValueOrDefault((key, u));
                        if (uOcc == 0) continue;
                        raw += title.Weight * (SmartDeckConstants.WindowWeight - SmartDeckConstants.WholeTitleWeight) * Math.Log2(1 + uOcc);
                        if (unitId == null) { unitId = u; unitOcc = uOcc; }
                    }
                }
                if (raw <= 0) continue;
                contributions.Add((new SmartDeckContribution(title.ParentDeckId, occ, unitId, unitOcc, 0), raw));
            }

            var total = contributions.Sum(c => c.Raw);
            result[key] = contributions.OrderByDescending(c => c.Raw)
                                       .Select(c => c.Item with { Share = total > 0 ? c.Raw / total : 0 })
                                       .ToList();
        }

        return result;
    }

    public async Task<DateTime?> GetLastRebuilt(string userId)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(SmartDeckCacheKeys.LastRebuilt(userId));
            return value.HasValue && long.TryParse(value, out var ticks) ? new DateTime(ticks, DateTimeKind.Utc) : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read smart deck rebuild stamp for {UserId}", userId);
            return null;
        }
    }

    private bool HasRank(long key, out int rank) => referenceCache.TryGetRank(key, out rank) && rank > 0;

    private static List<SmartDeckTitleInput> BuildInputs(SmartDeckSources sources,
        Dictionary<int, List<SmartDeckWordOccurrence>> parentWords, Dictionary<int, List<SmartDeckWordOccurrence>> windowWords)
        => sources.Titles.Select(title =>
        {
            var wholeTarget = sources.TitlesWithUnits.Contains(title.ParentDeckId)
                ? sources.Settings.RestTargetPercentage
                : sources.Settings.TargetPercentage;
            var parts = new List<SmartDeckPart>();
            if (sources.ContributesWholeTitle(title.ParentDeckId))
                parts.Add(new SmartDeckPart(title.ParentDeckId, SmartDeckConstants.WholeTitleWeight, parentWords.GetValueOrDefault(title.ParentDeckId, []), wholeTarget));
            if (title.Boosted && sources.Windows.TryGetValue(title.ParentDeckId, out var window))
                foreach (var unitId in window.WindowDeckIds)
                    parts.Add(new SmartDeckPart(unitId, SmartDeckConstants.WindowWeight - SmartDeckConstants.WholeTitleWeight,
                                                windowWords.GetValueOrDefault(unitId, []), sources.Settings.TargetPercentage));
            return new SmartDeckTitleInput(title.ParentDeckId, title.Weight, parts);
        }).ToList();

    private static int CountEligible(SmartDeckSettings settings, List<SmartDeckPreferenceRow> rows)
    {
        var excluded = settings.ExcludedDeckIds.ToHashSet();
        var manual = settings.PinnedDeckIds.Concat(settings.IncludedDeckIds).ToHashSet();
        var parents = rows.Where(r => r.ParentDeckId == null && !excluded.Contains(r.DeckId));
        var count = parents.Count(r => r.Status == DeckStatus.Ongoing || manual.Contains(r.DeckId)
                                       || (settings.WeighPlanning && r.Status == DeckStatus.Planning));
        var untouchedManual = manual.Count(id => !excluded.Contains(id) && rows.All(r => r.DeckId != id));
        return count + untouchedManual;
    }

    private async Task<Dictionary<int, List<SmartDeckWordOccurrence>>> LoadWords(List<int> deckIds, CancellationToken ct)
    {
        var result = deckIds.ToDictionary(id => id, _ => new List<SmartDeckWordOccurrence>());
        if (deckIds.Count == 0) return result;

        await foreach (var w in context.DeckWords.AsNoTracking()
                                      .Where(w => deckIds.Contains(w.DeckId))
                                      .Select(w => new { w.DeckId, w.WordId, w.ReadingIndex, w.Occurrences })
                                      .AsAsyncEnumerable().WithCancellation(ct))
            result[w.DeckId].Add(new SmartDeckWordOccurrence(w.WordId, w.ReadingIndex, w.Occurrences));

        return result;
    }

    private async Task<(HashSet<long> Excluded, HashSet<long> CardKeys)> LoadExclusionKeys(string userId, CancellationToken ct)
    {
        var keys = new HashSet<long>();
        var cardKeys = new HashSet<long>();
        // One conductor set for kana and derivation covers, the same one the study-batch picker uses.
        var known = new List<(int WordId, byte ReadingIndex)>();

        await foreach (var c in userContext.FsrsCards.AsNoTracking()
                                          .Where(c => c.UserId == userId)
                                          .Select(c => new { c.WordId, c.ReadingIndex })
                                          .AsAsyncEnumerable().WithCancellation(ct))
        {
            var cardKey = WordFormHelper.EncodeWordKey(c.WordId, c.ReadingIndex);
            keys.Add(cardKey);
            cardKeys.Add(cardKey);
            known.Add((c.WordId, c.ReadingIndex));
        }

        var setStates = await userContext.UserWordSetStates.AsNoTracking()
                                         .Where(s => s.UserId == userId)
                                         .Select(s => new { s.SetId, s.State })
                                         .ToListAsync(ct);
        if (setStates.Count > 0)
        {
            var setIds = setStates.Select(s => s.SetId).ToList();
            var conducting = setStates.Where(s => s.State is WordSetStateType.Mastered or WordSetStateType.Blacklisted)
                                      .Select(s => s.SetId).ToHashSet();

            var members = await context.WordSetMembers.AsNoTracking()
                                       .Where(m => setIds.Contains(m.SetId))
                                       .Select(m => new { m.SetId, m.WordId, m.ReadingIndex })
                                       .ToListAsync(ct);
            foreach (var m in members)
            {
                var ri = (byte)m.ReadingIndex;
                keys.Add(WordFormHelper.EncodeWordKey(m.WordId, ri));
                if (conducting.Contains(m.SetId)) known.Add((m.WordId, ri));
            }
        }

        WordFormHelper.ExpandKanaRedundancyKeys(siblingCache, known, keys);
        var categories = await DerivationSettingsHelper.GetEnabledCategories(memoryCache, userContext, userId);
        WordFormHelper.ExpandDerivationRedundancyKeys(derivationCache, categories, known, keys);
        return (keys, cardKeys);
    }

    private async Task<List<SmartDeckScoredWord>> ApplyPosFilter(List<SmartDeckScoredWord> ranked, string posFilter, int cap, CancellationToken ct)
    {
        string[] posTags;
        try
        {
            posTags = JsonSerializer.Deserialize<string[]>(posFilter) ?? [];
        }
        catch (JsonException)
        {
            posTags = [];
        }
        if (posTags.Length == 0) return ranked.Take(cap).ToList();

        var kept = new List<SmartDeckScoredWord>(Math.Min(ranked.Count, cap));
        for (var offset = 0; offset < ranked.Count && kept.Count < cap; offset += PosFilterChunk)
        {
            var chunk = ranked.Skip(offset).Take(PosFilterChunk).ToList();
            var ids = chunk.Select(w => w.WordId).Distinct().ToList();
            var matched = (await context.JMDictWords.AsNoTracking()
                                        .Where(w => ids.Contains(w.WordId) && w.PartsOfSpeech.Any(p => posTags.Contains(p)))
                                        .Select(w => w.WordId)
                                        .ToListAsync(ct)).ToHashSet();
            foreach (var word in chunk)
            {
                if (!matched.Contains(word.WordId)) continue;
                kept.Add(word);
                if (kept.Count >= cap) break;
            }
        }
        return kept;
    }

    private async Task WriteRows(int studyDeckId, List<SmartDeckScoredWord> ranked, CancellationToken ct)
    {
        if (userContext.Database.IsNpgsql())
        {
            await WriteRowsNpgsql(studyDeckId, ranked, ct);
            return;
        }

        await using var tx = await userContext.Database.BeginTransactionAsync(ct);
        await userContext.UserStudyDeckWords.Where(w => w.UserStudyDeckId == studyDeckId).ExecuteDeleteAsync(ct);
        userContext.UserStudyDeckWords.AddRange(ranked.Select((w, i) => new UserStudyDeckWord
        {
            UserStudyDeckId = studyDeckId, WordId = w.WordId, ReadingIndex = w.ReadingIndex,
            SortOrder = i + 1, Occurrences = ScaledScore(w.Score)
        }));
        await userContext.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task WriteRowsNpgsql(int studyDeckId, List<SmartDeckScoredWord> ranked, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)userContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var delete = new NpgsqlCommand("DELETE FROM \"user\".\"UserStudyDeckWords\" WHERE \"UserStudyDeckId\" = @d", conn, tx))
        {
            delete.Parameters.AddWithValue("d", studyDeckId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        await using (var writer = await conn.BeginBinaryImportAsync(
                         "COPY \"user\".\"UserStudyDeckWords\" (\"UserStudyDeckId\", \"WordId\", \"ReadingIndex\", \"SortOrder\", \"Occurrences\") FROM STDIN (FORMAT BINARY)", ct))
        {
            for (var i = 0; i < ranked.Count; i++)
            {
                await writer.StartRowAsync(ct);
                await writer.WriteAsync(studyDeckId, NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(ranked[i].WordId, NpgsqlDbType.Integer, ct);
                await writer.WriteAsync((short)ranked[i].ReadingIndex, NpgsqlDbType.Smallint, ct);
                await writer.WriteAsync(i + 1, NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(ScaledScore(ranked[i].Score), NpgsqlDbType.Integer, ct);
            }
            await writer.CompleteAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private static int ScaledScore(double score) => Math.Max(1, (int)Math.Round(score * SmartDeckConstants.OccurrencesScale));

    public async Task ClearLastRebuilt(string userId)
    {
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(SmartDeckCacheKeys.LastRebuilt(userId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear smart deck rebuild stamp for {UserId}", userId);
        }
    }

    private async Task StampRebuilt(string userId)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(SmartDeckCacheKeys.LastRebuilt(userId), DateTime.UtcNow.Ticks.ToString(), TimeSpan.FromDays(30));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to stamp smart deck rebuild for {UserId}", userId);
        }
    }
}
