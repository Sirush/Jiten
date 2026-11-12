using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services.ReadingCheck;

public interface IReadingCheckData
{
    Task<List<ReadingWorkDto>> GetWorksAsync(string userId);
    Task<ReadingWorkDto?> GetWorkAsync(string userId, int deckId);
    Task<List<int>> ResolveLeavesAsync(IReadOnlyCollection<int> deckIds);
    Task<List<ReadingCandidate>> GetCandidatesAsync(string userId, IReadOnlyList<int> leafDeckIds);
    Task<Dictionary<(int WordId, byte ReadingIndex), (string Text, string Reading, List<string> Meanings)>> DescribeAsync(
        IEnumerable<(int WordId, byte ReadingIndex)> keys);
}

public sealed class ReadingCheckData(
    IDbContextFactory<JitenDbContext> jitenFactory,
    UserDbContext userContext,
    IWordSetMemberCache wordSetCache,
    IMemoryCache cache) : IReadingCheckData
{
    private static readonly MemoryCacheEntryOptions CandidateCacheOptions = new() { SlidingExpiration = TimeSpan.FromMinutes(10) };

    /// <summary>A surface that is also a name and comes mostly from one work is most likely a character's name parsed as a common word.</summary>
    private const double NameConcentration = 0.8;

    /// <summary>A numeral and a counter or time unit, like 一冊, 三日間 or 何度も: knowing it says nothing about vocabulary.</summary>
    private static readonly Regex NumeralCounter = new(
        "^第?[〇一二三四五六七八九十百千万億兆何数幾両]+"
        + "(人|匹|頭|羽|本|枚|冊|台|個|つ|回|度|番|階|歳|才|年|月|日|時|分|秒|週|円|杯|着|足|軒|件|通|隻|機|発|組|名|位|号|部|点|曲|話|巻|章|品|社|校|割|倍|段|例|席|票|戦|勝|敗|周|行|字|語|代|期|区|か所|ヶ所|カ所|箇所|ヶ月|か月|カ月|箇月|世紀|年生)"
        + "(間|目|後|前|頃|も|半|以上|以内|ほど|ぐらい|くらい)?$",
        RegexOptions.Compiled);

    /// <summary>Irregular, rare, outdated or search-only spellings; asking about them tests the spelling, not the word.</summary>
    private static readonly HashSet<string> IrregularFormTags = new(StringComparer.OrdinalIgnoreCase) { "rK", "sK", "oK", "iK", "io" };

    private static readonly HashSet<string> ExcludedPos = new(
        CompositionRules.GrammaticalTags.Concat(["arch", "obs", "num", "ctr", "suf", "pref", "n-suf", "n-pref"]), StringComparer.Ordinal);

    private sealed record DeckRow(int DeckId, int? ParentDeckId, int DeckOrder, string OriginalTitle, string? RomajiTitle, string? EnglishTitle,
                                  string CoverName, MediaType MediaType, DateOnly ReleaseDate);

    private static readonly Expression<Func<Deck, DeckRow>> ToDeckRow =
        d => new DeckRow(d.DeckId, d.ParentDeckId, d.DeckOrder, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.CoverName, d.MediaType,
                         d.ReleaseDate);

    public async Task<List<ReadingWorkDto>> GetWorksAsync(string userId)
    {
        var entries = await userContext.UserMediaListEntries.AsNoTracking()
                                       .Where(e => e.UserId == userId && e.State != MediaListEntryState.Dropped)
                                       .Select(e => new { e.DeckId, e.State, e.UpdatedAt })
                                       .ToListAsync();
        if (entries.Count == 0) return [];

        var completed = entries.Where(e => e.State == MediaListEntryState.Completed).Select(e => e.DeckId).ToHashSet();
        var inProgress = entries.Where(e => e.State == MediaListEntryState.InProgress).Select(e => e.DeckId).ToHashSet();
        var lastTouched = entries.GroupBy(e => e.DeckId).ToDictionary(g => g.Key, g => g.Max(e => e.UpdatedAt));

        await using var context = await jitenFactory.CreateDbContextAsync();
        var listed = await LoadDecks(context, lastTouched.Keys.ToList());
        var roots = listed.Where(d => d.ParentDeckId == null).ToDictionary(d => d.DeckId);
        var missingParents = listed.Where(d => d.ParentDeckId is { } p && !roots.ContainsKey(p)).Select(d => d.ParentDeckId!.Value).Distinct().ToList();
        if (missingParents.Count > 0)
            foreach (var parent in await LoadDecks(context, missingParents))
                roots[parent.DeckId] = parent;
        var rootIds = roots.Keys.ToList();
        var children = await LoadChildren(context, rootIds);

        var rows = new List<(DateTime Touched, ReadingWorkDto Row)>();
        var singles = new List<DeckRow>();

        foreach (var root in roots.Values)
        {
            var units = children.GetValueOrDefault(root.DeckId);
            var touched = Touched(lastTouched, units?.Select(u => u.DeckId).Append(root.DeckId) ?? [root.DeckId]);
            if (units is not { Count: > 0 })
            {
                singles.Add(root);
                continue;
            }

            var rootCompleted = completed.Contains(root.DeckId);
            var dtoUnits = units.Select(u => Unit(u, completed.Contains(u.DeckId) || rootCompleted,
                                                  inProgress.Contains(u.DeckId))).ToList();
            if (dtoUnits.All(u => !u.Ticked && !u.InProgress) && !inProgress.Contains(root.DeckId)) continue;
            rows.Add((touched, Row($"deck-{root.DeckId}", root, dtoUnits)));
        }

        var seriesBySingle = await LoadSeries(context, singles.Select(s => s.DeckId).ToList());
        foreach (var group in singles.GroupBy(s => seriesBySingle.GetValueOrDefault(s.DeckId)))
        {
            if (group.Key is { } series && series.Members.Count > 1)
            {
                var units = series.Members.Select(m => Unit(m, completed.Contains(m.DeckId), inProgress.Contains(m.DeckId))).ToList();
                rows.Add((Touched(lastTouched, group.Select(g => g.DeckId)), new ReadingWorkDto(
                              $"series-{series.SeriesId}", series.OriginalTitle, series.RomajiTitle, series.EnglishTitle,
                              group.First().CoverName, group.First().MediaType, true, units)));
                continue;
            }

            foreach (var single in group.Where(s => completed.Contains(s.DeckId) || inProgress.Contains(s.DeckId)))
            {
                var unit = Unit(single, completed.Contains(single.DeckId), inProgress.Contains(single.DeckId));
                rows.Add((Touched(lastTouched, [single.DeckId]), Row($"deck-{single.DeckId}", single, [unit])));
            }
        }

        return rows.OrderByDescending(r => r.Touched).Select(r => r.Row).ToList();
    }

    public async Task<ReadingWorkDto?> GetWorkAsync(string userId, int deckId)
    {
        await using var context = await jitenFactory.CreateDbContextAsync();
        var deck = (await LoadDecks(context, [deckId])).FirstOrDefault();
        if (deck == null) return null;

        var root = deck.ParentDeckId is { } parentId ? (await LoadDecks(context, [parentId])).FirstOrDefault() ?? deck : deck;
        var units = (await LoadChildren(context, [root.DeckId])).GetValueOrDefault(root.DeckId);
        if (units is not { Count: > 0 })
            return Row($"deck-{root.DeckId}", root, [Unit(root, true, false)], fromMediaList: false);

        var relevant = units.Select(u => u.DeckId).Append(root.DeckId).ToList();
        var states = await userContext.UserMediaListEntries.AsNoTracking()
                                      .Where(e => e.UserId == userId && relevant.Contains(e.DeckId))
                                      .Select(e => new { e.DeckId, e.State })
                                      .ToListAsync();
        var completed = states.Where(s => s.State == MediaListEntryState.Completed).Select(s => s.DeckId).ToHashSet();
        var inProgress = states.Where(s => s.State == MediaListEntryState.InProgress).Select(s => s.DeckId).ToHashSet();

        var pickedWholeWork = deckId == root.DeckId;
        var dtoUnits = units.Select(u => Unit(u, completed.Contains(u.DeckId) || completed.Contains(root.DeckId) || u.DeckId == deckId
                                                 || (pickedWholeWork && !inProgress.Contains(u.DeckId)),
                                              inProgress.Contains(u.DeckId))).ToList();
        return Row($"deck-{root.DeckId}", root, dtoUnits, fromMediaList: false);
    }

    /// <summary>A parent expands to its children unless some of its own children were sent too, in which case only those count.</summary>
    public async Task<List<int>> ResolveLeavesAsync(IReadOnlyCollection<int> deckIds)
    {
        await using var context = await jitenFactory.CreateDbContextAsync();
        var ids = deckIds.Distinct().ToList();
        var rows = await context.Decks.AsNoTracking()
                                .Where(d => ids.Contains(d.DeckId) || (d.ParentDeckId != null && ids.Contains(d.ParentDeckId!.Value)))
                                .Select(d => new { d.DeckId, d.ParentDeckId })
                                .ToListAsync();
        var sent = ids.ToHashSet();
        var parents = rows.Where(d => sent.Contains(d.DeckId));
        var childrenByParent = rows.Where(d => d.ParentDeckId is { } p && sent.Contains(p))
                                   .GroupBy(d => d.ParentDeckId!.Value)
                                   .ToDictionary(g => g.Key, g => g.Select(c => c.DeckId).ToList());

        var leaves = new HashSet<int>();
        foreach (var deck in parents)
        {
            if (childrenByParent.TryGetValue(deck.DeckId, out var kids))
            {
                if (!kids.Any(sent.Contains))
                    leaves.UnionWith(kids);
            }
            else
            {
                leaves.Add(deck.DeckId);
            }
        }

        return leaves.ToList();
    }

    /// <summary>The selection's words are cached for every user; the user's own cards and sets are filtered out fresh on each call, so an import mid-setup is never asked about.</summary>
    public async Task<List<ReadingCandidate>> GetCandidatesAsync(string userId, IReadOnlyList<int> leafDeckIds)
    {
        var words = await GetSelectionWordsAsync(leafDeckIds);
        if (words.Count == 0) return [];

        var wordIds = words.Select(c => c.WordId).Distinct().ToList();
        var wordsWithCards = (await userContext.FsrsCards.AsNoTracking()
                                               .Where(c => c.UserId == userId && wordIds.Contains(c.WordId))
                                               .Select(c => c.WordId)
                                               .Distinct()
                                               .ToListAsync())
            .ToHashSet();
        var subscribed = await wordSetCache.LoadForUserAsync(userContext, userId);
        var setWordIds = subscribed.Sets.SelectMany(set => set.Members).Select(k => WordFormHelper.DecodeWordKey(k).WordId).ToHashSet();

        return words.Where(c => !wordsWithCards.Contains(c.WordId) && !setWordIds.Contains(c.WordId)).ToList();
    }

    private async Task<List<ReadingCandidate>> GetSelectionWordsAsync(IReadOnlyList<int> leafDeckIds)
    {
        var key = $"reading-check-words:{string.Join(',', leafDeckIds.Order())}";
        if (cache.TryGetValue(key, out List<ReadingCandidate>? cached) && cached != null) return cached;

        await using var context = await jitenFactory.CreateDbContextAsync();
        var grouped = await context.DeckWords.AsNoTracking()
                                   .Where(dw => leafDeckIds.Contains(dw.DeckId)
                                                && (dw.WordId < SentenceComprehension.NameEntryFloor || dw.WordId >= SentenceComprehension.NameEntryCeiling))
                                   .GroupBy(dw => new { dw.WordId, dw.ReadingIndex })
                                   .Select(g => new { g.Key.WordId, g.Key.ReadingIndex, Occ = g.Sum(x => x.Occurrences) })
                                   .Where(g => g.Occ >= ReadingCheckEngine.MinProbeOccurrences)
                                   .ToListAsync();

        var wordIds = grouped.Select(g => g.WordId).Distinct().ToList();
        var groupedKeys = grouped.Select(g => (g.WordId, (short)g.ReadingIndex)).ToHashSet();
        var spread = await LoadWorkSpread(context, leafDeckIds, wordIds);
        var formText = (await context.WordForms.AsNoTracking()
                                     .Where(wf => wordIds.Contains(wf.WordId))
                                     .Select(wf => new { wf.WordId, wf.ReadingIndex, wf.Text, wf.InfoTags })
                                     .ToListAsync())
                       .Where(f => groupedKeys.Contains((f.WordId, f.ReadingIndex)))
                       .ToDictionary(f => (f.WordId, f.ReadingIndex), f => (f.Text, Irregular: f.InfoTags != null && f.InfoTags.Any(IrregularFormTags.Contains)));

        var pos = (await context.JMDictWords.AsNoTracking()
                                .Where(w => wordIds.Contains(w.WordId))
                                .Select(w => new { w.WordId, w.PartsOfSpeech })
                                .ToListAsync())
            .ToDictionary(w => w.WordId, w => w.PartsOfSpeech);

        var ranks = (await context.WordFormFrequencies.AsNoTracking()
                                  .Where(f => wordIds.Contains(f.WordId) && f.FrequencyRank > 0)
                                  .Select(f => new { f.WordId, f.ReadingIndex, f.FrequencyRank })
                                  .ToListAsync())
            .ToDictionary(f => (f.WordId, f.ReadingIndex), f => f.FrequencyRank);

        var surfaces = formText.Values.Select(f => f.Text).Distinct().ToList();
        var nameSurfaces = (await context.WordForms.AsNoTracking()
                                         .Where(wf => wf.WordId >= SentenceComprehension.NameEntryFloor && wf.WordId < SentenceComprehension.NameEntryCeiling
                                                      && surfaces.Contains(wf.Text))
                                         .Select(wf => wf.Text)
                                         .Distinct()
                                         .ToListAsync())
            .ToHashSet();
        var homographs = (await context.WordForms.AsNoTracking()
                                       .Where(wf => (wf.WordId < SentenceComprehension.NameEntryFloor || wf.WordId >= SentenceComprehension.NameEntryCeiling)
                                                    && wf.FormType == JmDictFormType.KanjiForm && surfaces.Contains(wf.Text))
                                       .Select(wf => new { wf.Text, wf.WordId })
                                       .ToListAsync())
            .GroupBy(f => f.Text)
            .Where(g => g.Select(f => f.WordId).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        var commonKanji = await GetCommonKanjiAsync(context);
        var candidates = new List<ReadingCandidate>();
        foreach (var g in grouped)
        {
            var formKey = (g.WordId, (short)g.ReadingIndex);
            if (!formText.TryGetValue(formKey, out var form) || !IsCandidateSurface(form.Text)) continue;
            var (works, maxInOneWork) = spread.GetValueOrDefault((g.WordId, g.ReadingIndex), (1, g.Occ));
            if (nameSurfaces.Contains(form.Text) && maxInOneWork >= g.Occ * NameConcentration) continue;
            var tags = pos.GetValueOrDefault(g.WordId) ?? [];
            if (tags.Any(ExcludedPos.Contains)) continue;

            var rank = ranks.GetValueOrDefault(formKey, ReadingCheckEngine.UnrankedRank);
            var expression = tags.Contains("exp");
            var probeable = !form.Irregular && !homographs.Contains(form.Text);
            var uncommonKanji = form.Text.Where(JapaneseTextHelper.IsKanji).Distinct().Count(c => !commonKanji.Contains(c));
            var offset = ReadingCheckEngine.WordOffset(uncommonKanji, !expression && IsVerb(tags));
            candidates.Add(new ReadingCandidate(g.WordId, g.ReadingIndex, g.Occ, rank, works, probeable, offset, expression));
        }

        cache.Set(key, candidates, CandidateCacheOptions);
        return candidates;
    }

    /// <summary>Grouped as in the prod measurement: adjectives and adverbs not tagged as nouns don't count, while suru nouns do.</summary>
    private static bool IsVerb(List<string> tags)
        => !(tags.Any(AdjectiveTags.Contains) && !tags.Contains("n"))
           && !(tags.Any(t => t is "adv" or "adv-to") && !tags.Contains("n"))
           && tags.Any(t => t.StartsWith('v'));

    private static readonly HashSet<string> AdjectiveTags = ["adj-i", "adj-na", "adj-no", "adj-t", "adj-f", "adj-ix"];

    private async Task<HashSet<char>> GetCommonKanjiAsync(JitenDbContext context)
    {
        const string key = "reading-check-common-kanji";
        if (cache.TryGetValue(key, out HashSet<char>? cached) && cached != null) return cached;

        var texts = await (from ff in context.WordFormFrequencies.AsNoTracking()
                           join wf in context.WordForms.AsNoTracking() on new { ff.WordId, ff.ReadingIndex } equals new { wf.WordId, wf.ReadingIndex }
                           where ff.FrequencyRank >= 1 && ff.FrequencyRank <= ReadingCheckEngine.CommonKanjiRank
                           select wf.Text).ToListAsync();
        var kanji = texts.SelectMany(t => t).Where(JapaneseTextHelper.IsKanji).ToHashSet();
        cache.Set(key, kanji, TimeSpan.FromHours(24));
        return kanji;
    }

    /// <summary>Kana-only words, numbers and numeral-counter pairs never become candidates.</summary>
    private static bool IsCandidateSurface(string text)
        => text.Any(JapaneseTextHelper.IsKanjiOrIterationMark)
           && !text.Any(c => c is >= '0' and <= '9' or >= '０' and <= '９' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= 'Ａ' and <= 'Ｚ' or >= 'ａ' and <= 'ｚ')
           && !NumeralCounter.IsMatch(text);

    /// <summary>Volumes of one series count as one work, so a character name spread over a long series still reads as concentrated.</summary>
    private static async Task<Dictionary<(int WordId, byte ReadingIndex), (int Works, int MaxInOneWork)>> LoadWorkSpread(
        JitenDbContext context, IReadOnlyList<int> leafDeckIds, List<int> wordIds)
    {
        var perWork = await (from dw in context.DeckWords.AsNoTracking()
                             join d in context.Decks.AsNoTracking() on dw.DeckId equals d.DeckId
                             where leafDeckIds.Contains(dw.DeckId) && wordIds.Contains(dw.WordId)
                             group dw by new { dw.WordId, dw.ReadingIndex, Work = d.ParentDeckId ?? d.DeckId }
                             into g
                             select new { g.Key.WordId, g.Key.ReadingIndex, Occ = g.Sum(x => x.Occurrences) })
            .ToListAsync();

        return perWork.GroupBy(r => (r.WordId, r.ReadingIndex))
                      .ToDictionary(g => g.Key, g => (g.Count(), g.Max(r => r.Occ)));
    }


    public async Task<Dictionary<(int WordId, byte ReadingIndex), (string Text, string Reading, List<string> Meanings)>> DescribeAsync(
        IEnumerable<(int WordId, byte ReadingIndex)> keys)
    {
        var keyList = keys.Distinct().ToList();
        var wordIds = keyList.Select(k => k.WordId).Distinct().ToList();

        await using var context = await jitenFactory.CreateDbContextAsync();
        var forms = (await context.WordForms.AsNoTracking()
                                  .Where(wf => wordIds.Contains(wf.WordId))
                                  .Select(wf => new JmDictWordForm
                                  {
                                      WordId = wf.WordId, ReadingIndex = wf.ReadingIndex, Text = wf.Text, RubyText = wf.RubyText, FormType = wf.FormType
                                  })
                                  .ToListAsync())
                    .ToLookup(f => f.WordId);
        var definitions = (await context.Definitions.AsNoTracking()
                                        .Where(d => wordIds.Contains(d.WordId) && d.IsActiveInLatestSource)
                                        .Select(d => new { d.WordId, d.SenseIndex, d.EnglishMeanings, d.RestrictedToReadingIndices })
                                        .ToListAsync())
                          .ToLookup(d => d.WordId);

        var result = new Dictionary<(int, byte), (string, string, List<string>)>();
        foreach (var (wordId, readingIndex) in keyList)
        {
            var wordForms = forms[wordId];
            var form = wordForms.FirstOrDefault(f => f.ReadingIndex == readingIndex);
            if (form == null) continue;

            var reading = RubyTextHelper.FormReading(form, wordForms);

            var meanings = definitions[wordId].Where(d => d.RestrictedToReadingIndices == null || d.RestrictedToReadingIndices.Count == 0
                                                          || d.RestrictedToReadingIndices.Contains(readingIndex))
                                      .OrderBy(d => d.SenseIndex)
                                      .Take(3)
                                      .Select(d => string.Join(", ", d.EnglishMeanings.Take(3)))
                                      .Where(m => m.Length > 0)
                                      .ToList();

            result[(wordId, readingIndex)] = (form.Text, reading, meanings);
        }

        return result;
    }

    private static async Task<List<DeckRow>> LoadDecks(JitenDbContext context, List<int> ids)
        => await context.Decks.AsNoTracking()
                        .Where(d => ids.Contains(d.DeckId))
                        .Select(ToDeckRow)
                        .ToListAsync();

    private static async Task<Dictionary<int, List<DeckRow>>> LoadChildren(JitenDbContext context, List<int> parentIds)
        => (await context.Decks.AsNoTracking()
                         .Where(d => d.ParentDeckId != null && parentIds.Contains(d.ParentDeckId!.Value))
                         .Select(ToDeckRow)
                         .ToListAsync())
           .GroupBy(d => d.ParentDeckId!.Value)
           .ToDictionary(g => g.Key, g => g.OrderBy(d => d.DeckOrder).ToList());

    private sealed record SeriesInfo(int SeriesId, string OriginalTitle, string? RomajiTitle, string? EnglishTitle, List<DeckRow> Members);

    private static async Task<Dictionary<int, SeriesInfo>> LoadSeries(JitenDbContext context, List<int> deckIds)
    {
        var memberships = await context.SeriesMembers.AsNoTracking()
                                       .Where(m => deckIds.Contains(m.DeckId))
                                       .Select(m => new { m.DeckId, m.SeriesId })
                                       .ToListAsync();
        if (memberships.Count == 0) return [];

        var seriesIds = memberships.Select(m => m.SeriesId).Distinct().ToList();
        var series = await context.Series.AsNoTracking()
                                  .Where(s => seriesIds.Contains(s.SeriesId))
                                  .Select(s => new
                                  {
                                      s.SeriesId, s.OriginalTitle, s.RomajiTitle, s.EnglishTitle,
                                      Members = s.Members.Where(m => m.Deck.ParentDeckId == null)
                                                 .Select(m => new DeckRow(m.Deck.DeckId, null, 0, m.Deck.OriginalTitle, m.Deck.RomajiTitle,
                                                                          m.Deck.EnglishTitle, m.Deck.CoverName, m.Deck.MediaType,
                                                                          m.Deck.ReleaseDate))
                                                 .ToList()
                                  })
                                  .ToListAsync();

        var byId = series.ToDictionary(s => s.SeriesId,
                                       s => new SeriesInfo(s.SeriesId, s.OriginalTitle, s.RomajiTitle, s.EnglishTitle,
                                                           s.Members.OrderBy(m => FranchiseNaming.ReleaseOrder(m.ReleaseDate))
                                                            .ThenBy(m => m.DeckId).ToList()));
        return memberships.GroupBy(m => m.DeckId).ToDictionary(g => g.Key, g => byId[g.First().SeriesId]);
    }

    private static ReadingUnitDto Unit(DeckRow deck, bool ticked, bool inProgress)
        => new(deck.DeckId, deck.OriginalTitle, inProgress && !ticked, ticked);

    private static ReadingWorkDto Row(string key, DeckRow deck, List<ReadingUnitDto> units, bool fromMediaList = true)
        => new(key, deck.OriginalTitle, deck.RomajiTitle, deck.EnglishTitle, deck.CoverName, deck.MediaType, fromMediaList, units);

    private static DateTime Touched(Dictionary<int, DateTime> lastTouched, IEnumerable<int> ids)
        => ids.Select(id => lastTouched.GetValueOrDefault(id)).DefaultIfEmpty(DateTime.MinValue).Max();
}
