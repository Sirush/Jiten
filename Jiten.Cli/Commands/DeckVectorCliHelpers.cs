using Jiten.Core;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Cli.Commands;

/// <summary>
/// Shared DB-hydration helpers for the deck-vector CLI commands.
/// </summary>
public static class DeckVectorCliHelpers
{
    public static async Task<Dictionary<int, string>> LoadTitles(JitenDbContext db, List<int> deckIds)
    {
        return await db.Decks.AsNoTracking()
                       .Where(d => deckIds.Contains(d.DeckId))
                       .Select(d => new { d.DeckId, Title = d.RomajiTitle ?? d.OriginalTitle })
                       .ToDictionaryAsync(d => d.DeckId, d => d.Title);
    }

    public static async Task<Dictionary<int, string>> LoadWordTexts(JitenDbContext db, IReadOnlyCollection<int> wordIds)
    {
        var forms = await db.WordForms.AsNoTracking()
                            .Where(f => wordIds.Contains(f.WordId))
                            .Select(f => new { f.WordId, f.ReadingIndex, f.Text })
                            .ToListAsync();
        return forms.GroupBy(f => f.WordId)
                    .ToDictionary(g => g.Key, g => g.OrderBy(f => f.ReadingIndex).First().Text);
    }
}
