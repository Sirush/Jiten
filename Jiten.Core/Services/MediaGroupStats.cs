using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.Services;

public class MediaGroupStatsDto
{
    public long CharacterCount { get; set; }
    public int UniqueWordCount { get; set; }

    /// <summary>Character-weighted adjusted difficulty, 0-5; -1 when unknown.</summary>
    public float Difficulty { get; set; } = -1;
}

public static class MediaGroupStats
{
    private const long DeckWordIdSpan = 1L << 40;
    private const int UndatedDayKey = 10_000 * 366;

    public static async Task<MediaGroupStatsDto> ComputeAsync(JitenDbContext db, IReadOnlyCollection<int> deckIds, CancellationToken ct = default)
    {
        var ids = deckIds.Distinct().ToList();
        var decks = await db.Decks.AsNoTracking()
                            .Where(d => ids.Contains(d.DeckId))
                            .Select(d => new Deck
                            {
                                CharacterCount = d.CharacterCount, Difficulty = d.Difficulty, DifficultyOverride = d.DifficultyOverride,
                                DeckDifficulty = d.DeckDifficulty
                            })
                            .ToListAsync(ct);

        // Unscored decks hold 0, so they are left out rather than averaged in as trivial.
        var scored = decks.Where(d => d.GetDifficulty() > 0).ToList();

        return new MediaGroupStatsDto
        {
            CharacterCount = decks.Sum(d => (long)d.CharacterCount),
            UniqueWordCount = await db.DeckWords.AsNoTracking()
                                      .Where(dw => ids.Contains(dw.DeckId))
                                      .Select(dw => new { dw.WordId, dw.ReadingIndex })
                                      .Distinct()
                                      .CountAsync(ct),
            Difficulty = scored.Count == 0 ? -1 : (float)Math.Round(Deck.WeightedByCharacters(scored, d => d.GetAdjustedDifficulty()), 2)
        };
    }

    /// <summary>One row per word, occurrences summed; DeckWordId is a first-appearance key led by release day, since a reparse moves a deck's row ids last.</summary>
    public static IQueryable<DeckWord> MergeWords(IQueryable<DeckWord> deckWords, IQueryable<Deck> decks) =>
        deckWords.Join(decks, dw => dw.DeckId, d => d.DeckId, (dw, d) => new
                 {
                     dw.WordId,
                     dw.ReadingIndex,
                     dw.Occurrences,
                     ChronoKey = (d.ReleaseDate >= FranchiseNaming.UnknownReleaseCutoff
                                     ? d.ReleaseDate.Year * 366 + d.ReleaseDate.DayOfYear
                                     : UndatedDayKey) * DeckWordIdSpan
                                 + dw.DeckWordId
                 })
                 .GroupBy(w => new { w.WordId, w.ReadingIndex })
                 .Select(g => new DeckWord
                 {
                     DeckWordId = g.Min(w => w.ChronoKey),
                     WordId = g.Key.WordId,
                     ReadingIndex = g.Key.ReadingIndex,
                     Occurrences = g.Sum(w => w.Occurrences)
                 });
}
