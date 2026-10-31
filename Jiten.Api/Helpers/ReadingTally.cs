using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

/// <summary>How much of what a user read counts toward their totals: rereads add again, a typed count replaces Jiten's, and a series is tallied with its volumes.</summary>
public static class ReadingTally
{
    public sealed record CompletedDeckInfo(int DeckId, int? ParentDeckId, MediaType MediaType, int CharacterCount, int WordCount);

    /// <summary>Effective set + leaf units per effective deck</summary>
    public static (List<CompletedDeckInfo> EffectiveDecks, Dictionary<int, int> UnitCounts) ResolveCompletedUnits(
        IReadOnlyList<CompletedDeckInfo> allCompletedDecks,
        IReadOnlyDictionary<int, int> childCounts)
    {
        var completedRootIds = allCompletedDecks.Where(d => d.ParentDeckId == null).Select(d => d.DeckId).ToHashSet();

        var effectiveDecks = allCompletedDecks
                             .Where(d => d.ParentDeckId == null || !completedRootIds.Contains(d.ParentDeckId.Value))
                             .ToList();

        var unitCounts = effectiveDecks.ToDictionary(d => d.DeckId,
                                                     d => d.ParentDeckId == null
                                                         ? Math.Max(childCounts.GetValueOrDefault(d.DeckId, 0), 1)
                                                         : 1);

        return (effectiveDecks, unitCounts);
    }

    /// <summary>One completed entry; SeriesEntryId links a volume's completion to the series pass it was read on.</summary>
    public sealed record CompletedEntry(long Id, int? CharactersRead, long? SeriesEntryId = null, DateOnly? FinishedOn = null);

    /// <summary>Each completed entry adds the deck again, its typed count replacing Jiten's characters; a series with completed volumes uses its tally.</summary>
    public static (long Characters, long Words) SumCompletedReading(IEnumerable<CompletedDeckInfo> decks,
                                                                    IReadOnlyDictionary<int, List<CompletedEntry>> completedEntriesByDeck,
                                                                    IReadOnlyDictionary<int, SeriesTotals>? seriesTotals = null)
    {
        long characters = 0, words = 0;
        foreach (var deck in decks)
        {
            if (seriesTotals?.GetValueOrDefault(deck.DeckId) is { } totals)
            {
                characters += totals.Characters;
                words += totals.Words;
                continue;
            }

            var passes = completedEntriesByDeck.TryGetValue(deck.DeckId, out var entries) && entries.Count > 0
                ? entries.Select(e => e.CharactersRead).ToList()
                : [null];
            words += (long)deck.WordCount * passes.Count;
            characters += passes.Sum(c => c ?? (long)deck.CharacterCount);
        }

        return (characters, words);
    }

    /// <summary>A completed series' reading, its completed passes and the volume reads outside them included; Standalone is the latter alone.</summary>
    public sealed record SeriesTotals(long Characters, long Words, long StandaloneCharacters);

    public sealed record UnfinishedDeckInfo(int DeckId, int? ParentDeckId, MediaType MediaType, bool HasChildren);

    /// <summary>Typed unfinished counts per deck; a series' own count only stands for what goes beyond the volumes already counted as completed.</summary>
    public static Dictionary<int, (MediaType MediaType, long Characters)> ResolveUnfinishedCharacters(
        IReadOnlyList<UnfinishedDeckInfo> unfinishedDecks, IReadOnlyDictionary<int, long> unfinishedByDeck,
        IReadOnlyList<CompletedDeckInfo> completedDecks, IReadOnlyDictionary<int, List<CompletedEntry>> completedEntriesByDeck,
        IReadOnlySet<int> completedRootIds, IReadOnlySet<int> rereadRootIds, IReadOnlyDictionary<int, SeriesTotals> seriesTotals)
    {
        // A completed series counts its volumes whole, so their partial counts drop out unless the series is being read again.
        var settledRootIds = completedRootIds.Where(id => !rereadRootIds.Contains(id)).ToHashSet();
        var result = unfinishedDecks.Where(d => d.ParentDeckId is not { } p || !settledRootIds.Contains(p))
                                    .ToDictionary(d => d.DeckId, d => (d.MediaType, unfinishedByDeck[d.DeckId]));

        foreach (var series in unfinishedDecks.Where(d => d.HasChildren && !settledRootIds.Contains(d.DeckId)))
        {
            var completedVolumes = completedRootIds.Contains(series.DeckId)
                ? seriesTotals.GetValueOrDefault(series.DeckId)?.StandaloneCharacters ?? 0
                : SumCompletedReading(completedDecks.Where(c => c.ParentDeckId == series.DeckId), completedEntriesByDeck).Characters;
            var partialVolumes = unfinishedDecks.Where(d => d.ParentDeckId == series.DeckId).Select(d => d.DeckId).ToList();
            var partial = partialVolumes.Sum(id => unfinishedByDeck[id]);
            foreach (var id in partialVolumes)
                result.Remove(id);

            result[series.DeckId] = (series.MediaType, Math.Max(Math.Max(unfinishedByDeck[series.DeckId] - completedVolumes, partial), 0));
        }

        return result;
    }

    /// <summary>Totals of each completed series that has completed volumes, through <see cref="MediaListEntryHelper.TallySeries"/>.</summary>
    public static Dictionary<int, SeriesTotals> ResolveSeriesTotals(IReadOnlyList<CompletedDeckInfo> allCompletedDecks,
                                                                   IReadOnlyDictionary<int, List<CompletedEntry>> completedEntriesByDeck)
    {
        var result = new Dictionary<int, SeriesTotals>();
        foreach (var series in TallyCompletedSeries(allCompletedDecks, completedEntriesByDeck))
        {
            // A typed series count stands for everything read on that pass, volumes included.
            var passCharacters = series.Passes.Sum(p => p.CharactersRead ?? series.Tally.PassCharacters[p.Id]);
            result[series.Series.DeckId] = new SeriesTotals(passCharacters + series.Tally.StandaloneCharacters,
                                                            (long)series.Series.WordCount * series.Passes.Count +
                                                            series.Tally.StandaloneDeckIds.Sum(id => (long)series.Volumes[id].WordCount),
                                                            series.Tally.StandaloneCharacters);
        }

        return result;
    }

    private sealed record CompletedSeries(CompletedDeckInfo Series, IReadOnlyDictionary<int, CompletedDeckInfo> Volumes, IReadOnlyList<CompletedEntry> Passes,
                                          IReadOnlyList<(CompletedDeckInfo Volume, CompletedEntry Entry)> Completions, SeriesTally Tally);

    private static IEnumerable<CompletedSeries> TallyCompletedSeries(IReadOnlyList<CompletedDeckInfo> allCompletedDecks,
                                                                     IReadOnlyDictionary<int, List<CompletedEntry>> completedEntriesByDeck)
    {
        var volumesBySeries = allCompletedDecks.Where(d => d.ParentDeckId != null).GroupBy(d => d.ParentDeckId!.Value).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var series in allCompletedDecks.Where(d => d.ParentDeckId == null && volumesBySeries.ContainsKey(d.DeckId)))
        {
            var volumes = volumesBySeries[series.DeckId].ToDictionary(v => v.DeckId);
            // A series marked Completed before entries existed counts as one untyped pass.
            List<CompletedEntry> passes = completedEntriesByDeck.GetValueOrDefault(series.DeckId) is { Count: > 0 } seriesEntries
                ? seriesEntries
                : [new CompletedEntry(0, null)];
            var completions = volumes.Values
                                     .SelectMany(v => (completedEntriesByDeck.GetValueOrDefault(v.DeckId) ?? []).Select(e => (Volume: v, Entry: e)))
                                     .OrderBy(c => c.Entry.Id)
                                     .ToList();

            var tally = MediaListEntryHelper.TallySeries(series.CharacterCount, passes.Select(p => p.Id).ToList(),
                                                         completions.Select(c => new VolumeCompletion(c.Volume.DeckId, c.Volume.CharacterCount,
                                                                                                      c.Entry.CharactersRead, c.Entry.SeriesEntryId))
                                                                    .ToList());
            yield return new CompletedSeries(series, volumes, passes, completions, tally);
        }
    }

    /// <summary>A user's reading as the accomplishment totals count it; <see cref="UnfinishedTitles"/> maps each unfinished key to its title.</summary>
    public sealed record ReadingLedger(
        IReadOnlyList<CompletedDeckInfo> AllCompletedDecks,
        IReadOnlyList<CompletedDeckInfo> CompletedDecks,
        IReadOnlyDictionary<int, int> UnitCounts,
        IReadOnlyDictionary<int, List<CompletedEntry>> CompletedEntriesByDeck,
        IReadOnlyDictionary<int, SeriesTotals> SeriesTotals,
        IReadOnlyDictionary<int, (MediaType MediaType, long Characters)> Unfinished,
        IReadOnlyDictionary<int, int> UnfinishedTitles)
    {
        public bool IsEmpty => CompletedDecks.Count == 0 && Unfinished.Count == 0;

        /// <summary>Whole works only; volumes finished in an unfinished series count toward reading, not toward titles.</summary>
        public IEnumerable<int> FinishedTitleIds => CompletedDecks.Where(d => d.ParentDeckId == null).Select(d => d.DeckId);
    }

    public static async Task<ReadingLedger> LoadAsync(JitenDbContext context, UserDbContext userContext, string userId)
    {
        var statusCompletedIds = await userContext.UserDeckPreferences
                                                  .Where(udp => udp.UserId == userId && udp.Status == DeckStatus.Completed)
                                                  .Select(udp => udp.DeckId)
                                                  .ToListAsync();

        var entries = await userContext.UserMediaListEntries
                                       .AsNoTracking()
                                       .Where(r => r.UserId == userId)
                                       .OrderBy(r => r.Id)
                                       .Select(r => new { r.Id, r.DeckId, r.State, r.CharactersRead, r.SeriesEntryId, r.FinishedOn })
                                       .ToListAsync();

        // A title counts as completed once any of its entries was, so reading it again keeps it in the totals.
        var completedEntriesByDeck = entries.Where(r => r.State == MediaListEntryState.Completed)
                                            .GroupBy(r => r.DeckId)
                                            .ToDictionary(g => g.Key,
                                                          g => g.Select(r => new CompletedEntry(r.Id, r.CharactersRead, r.SeriesEntryId, r.FinishedOn)).ToList());
        var completedDeckIds = statusCompletedIds.Union(completedEntriesByDeck.Keys).ToList();

        var unfinishedByDeck = entries.Where(r => r.State != MediaListEntryState.Completed && r.CharactersRead != null)
                                      .GroupBy(r => r.DeckId)
                                      .ToDictionary(g => g.Key, g => g.Sum(r => (long)r.CharactersRead!.Value));

        var allCompletedDecks = completedDeckIds.Count == 0
            ? []
            : await context.Decks
                           .AsNoTracking()
                           .Where(d => completedDeckIds.Contains(d.DeckId))
                           .Select(d => new CompletedDeckInfo(d.DeckId, d.ParentDeckId, d.MediaType, d.CharacterCount, d.WordCount))
                           .ToListAsync();

        var completedRootIds = allCompletedDecks.Where(d => d.ParentDeckId == null).Select(d => d.DeckId).ToList();

        var childCounts = completedRootIds.Count == 0
            ? new Dictionary<int, int>()
            : await context.Decks
                           .AsNoTracking()
                           .Where(d => d.ParentDeckId != null && completedRootIds.Contains(d.ParentDeckId.Value))
                           .GroupBy(d => d.ParentDeckId!.Value)
                           .Select(g => new { ParentDeckId = g.Key, Count = g.Count() })
                           .ToDictionaryAsync(g => g.ParentDeckId, g => g.Count);

        var (completedDecks, unitCounts) = ResolveCompletedUnits(allCompletedDecks, childCounts);

        var unfinishedDeckIds = unfinishedByDeck.Keys.ToList();
        var unfinishedDecks = unfinishedDeckIds.Count == 0
            ? []
            : await context.Decks
                           .AsNoTracking()
                           .Where(d => unfinishedDeckIds.Contains(d.DeckId))
                           .Select(d => new UnfinishedDeckInfo(d.DeckId, d.ParentDeckId, d.MediaType, d.Children.Any()))
                           .ToListAsync();

        var seriesTotals = ResolveSeriesTotals(allCompletedDecks, completedEntriesByDeck);
        var rereadRootIds = completedRootIds.Count == 0
            ? []
            : (await userContext.UserDeckPreferences
                                .AsNoTracking()
                                .Where(p => p.UserId == userId && completedRootIds.Contains(p.DeckId) &&
                                            p.CurrentEntry!.State == MediaListEntryState.InProgress)
                                .Select(p => p.DeckId)
                                .ToListAsync()).ToHashSet();
        var unfinished = ResolveUnfinishedCharacters(unfinishedDecks, unfinishedByDeck, completedDecks, completedEntriesByDeck,
                                                     completedRootIds.ToHashSet(), rereadRootIds, seriesTotals);

        return new ReadingLedger(allCompletedDecks, completedDecks, unitCounts, completedEntriesByDeck, seriesTotals, unfinished,
                                 unfinishedDecks.ToDictionary(d => d.DeckId, d => d.ParentDeckId ?? d.DeckId));
    }

    /// <summary>Completed reading attributed to a title and a finish date; FinishedOn is null for a completion recorded without one.</summary>
    public sealed record ReadingCredit(int TitleId, MediaType MediaType, DateOnly? FinishedOn, long Characters);

    /// <summary>Splits <see cref="SumCompletedReading"/> by completion: a series' volumes land on their own dates and its pass adds only what they leave.</summary>
    public static List<ReadingCredit> CreditCompletedReading(ReadingLedger ledger)
    {
        var credits = new List<ReadingCredit>();
        foreach (var deck in ledger.CompletedDecks.Where(d => !ledger.SeriesTotals.ContainsKey(d.DeckId)))
        {
            var titleId = deck.ParentDeckId ?? deck.DeckId;
            if (ledger.CompletedEntriesByDeck.GetValueOrDefault(deck.DeckId) is { Count: > 0 } entries)
                credits.AddRange(entries.Select(e => new ReadingCredit(titleId, deck.MediaType, e.FinishedOn, e.CharactersRead ?? deck.CharacterCount)));
            else
                credits.Add(new ReadingCredit(titleId, deck.MediaType, null, deck.CharacterCount));
        }

        foreach (var series in TallyCompletedSeries(ledger.AllCompletedDecks, ledger.CompletedEntriesByDeck))
        {
            var (titleId, mediaType) = (series.Series.DeckId, series.Series.MediaType);
            var indexes = Enumerable.Range(0, series.Completions.Count).ToList();
            ReadingCredit VolumeCredit(int i) =>
                new(titleId, mediaType, series.Completions[i].Entry.FinishedOn, series.Completions[i].Entry.CharactersRead ?? series.Completions[i].Volume.CharacterCount);

            foreach (var pass in series.Passes)
            {
                if (pass.CharactersRead is { } typed)
                {
                    credits.Add(new ReadingCredit(titleId, mediaType, pass.FinishedOn, typed));
                    continue;
                }

                var remaining = series.Tally.PassCharacters[pass.Id];
                foreach (var volume in indexes.Where(i => series.Tally.PassOf[i] == pass.Id).Select(VolumeCredit))
                {
                    var characters = Math.Min(volume.Characters, remaining);
                    credits.Add(volume with { Characters = characters });
                    remaining -= characters;
                }

                credits.Add(new ReadingCredit(titleId, mediaType, pass.FinishedOn, remaining));
            }

            credits.AddRange(indexes.Where(i => series.Tally.PassOf[i] == null).Select(VolumeCredit));
        }

        return credits;
    }

    /// <summary>Characters read per title from <paramref name="credits"/>, plus reading in progress when <paramref name="withUnfinished"/>.</summary>
    public static Dictionary<int, long> CharactersByTitle(ReadingLedger ledger, IEnumerable<ReadingCredit> credits, bool withUnfinished)
    {
        var result = credits.GroupBy(c => c.TitleId).ToDictionary(g => g.Key, g => g.Sum(c => c.Characters));
        if (!withUnfinished)
            return result;

        foreach (var (deckId, unfinished) in ledger.Unfinished)
        {
            var titleId = ledger.UnfinishedTitles.GetValueOrDefault(deckId, deckId);
            result[titleId] = result.GetValueOrDefault(titleId) + unfinished.Characters;
        }

        return result;
    }
}
