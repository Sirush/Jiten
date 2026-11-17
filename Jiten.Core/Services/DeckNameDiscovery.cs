using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.Services;

/// <summary>
/// Names the parser found in a deck's own text, stored on the root deck so a later partial parse (a new subdeck) cuts them the same way.
/// A full parse must not receive them: it would stop finding them and the next one would lose them.
/// </summary>
public static class DeckNameDiscovery
{
    public static List<DeckDictionaryEntry>? CuratedOnly(IEnumerable<DeckDictionaryEntry>? entries)
    {
        var curated = entries?.Where(e => e.EntryType != DeckDictionaryEntryType.DiscoveredName).ToList();
        return curated is { Count: > 0 } ? curated : null;
    }

    public static List<DeckDictionaryEntry> ToEntries(IEnumerable<string> names, IEnumerable<DeckDictionaryEntry>? curated, int deckId = 0)
    {
        var taken = new HashSet<string>(curated?.Select(e => e.Surface.Trim()) ?? [], StringComparer.Ordinal);
        return names.Where(taken.Add)
                    .Select(n => new DeckDictionaryEntry { DeckId = deckId, Surface = n, EntryType = DeckDictionaryEntryType.DiscoveredName })
                    .ToList();
    }

    /// <summary>A discovered name that a person or a provider now lists becomes a curated one; (DeckId, Surface) is unique.</summary>
    public static void Promote(Deck deck, IEnumerable<string> curatedNames)
    {
        var names = new HashSet<string>(curatedNames.Select(n => n.Trim()), StringComparer.Ordinal);
        foreach (var entry in deck.DictionaryEntries)
            if (entry.EntryType == DeckDictionaryEntryType.DiscoveredName && names.Contains(entry.Surface.Trim()))
                entry.EntryType = DeckDictionaryEntryType.Name;
    }

    public static async Task ReplaceAsync(JitenDbContext context, int deckId, IReadOnlyCollection<string> names, CancellationToken ct = default)
    {
        var existing = await context.DeckDictionaryEntries.Where(e => e.DeckId == deckId).ToListAsync(ct);
        context.DeckDictionaryEntries.RemoveRange(existing.Where(e => e.EntryType == DeckDictionaryEntryType.DiscoveredName && !names.Contains(e.Surface)));

        var kept = existing.Select(e => e.Surface).ToHashSet(StringComparer.Ordinal);
        context.DeckDictionaryEntries.AddRange(names.Where(n => !kept.Contains(n))
                                                    .Select(n => new DeckDictionaryEntry
                                                    {
                                                        DeckId = deckId, Surface = n, EntryType = DeckDictionaryEntryType.DiscoveredName
                                                    }));
        await context.SaveChangesAsync(ct);
    }
}
