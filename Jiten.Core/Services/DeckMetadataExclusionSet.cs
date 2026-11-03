using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.Services;

/// <summary>A deck's removed values, consulted by provider imports so a person's removal sticks.</summary>
public sealed class DeckMetadataExclusionSet
{
    public static readonly DeckMetadataExclusionSet Empty = new([]);

    private readonly HashSet<int> _genres = [];
    private readonly HashSet<int> _tags = [];

    private readonly HashSet<string> _aliases = new(ValueComparer(DeckMetadataExclusionKind.Alias));
    private readonly HashSet<string> _characterNames = new(ValueComparer(DeckMetadataExclusionKind.CharacterName));

    /// <summary>Aliases compare case-insensitively, matching the provider alias merge that treats titles differing only in case as one.</summary>
    public static StringComparer ValueComparer(DeckMetadataExclusionKind kind) =>
        kind == DeckMetadataExclusionKind.Alias ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public DeckMetadataExclusionSet(IEnumerable<DeckMetadataExclusion> rows)
    {
        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case DeckMetadataExclusionKind.Genre when int.TryParse(row.Value, out var genre):
                    _genres.Add(genre);
                    break;
                case DeckMetadataExclusionKind.Tag when int.TryParse(row.Value, out var tagId):
                    _tags.Add(tagId);
                    break;
                case DeckMetadataExclusionKind.Alias:
                    _aliases.Add(row.Value.Trim());
                    break;
                case DeckMetadataExclusionKind.CharacterName:
                    _characterNames.Add(row.Value.Trim());
                    break;
            }
        }
    }

    public static async Task<DeckMetadataExclusionSet> LoadAsync(JitenDbContext db, int deckId, CancellationToken ct = default)
    {
        var rows = await db.DeckMetadataExclusions.AsNoTracking().Where(e => e.DeckId == deckId).ToListAsync(ct);
        return rows.Count == 0 ? Empty : new DeckMetadataExclusionSet(rows);
    }

    public bool ExcludesGenre(Genre genre) => _genres.Contains((int)genre);

    public bool ExcludesTag(int tagId) => _tags.Contains(tagId);

    public bool ExcludesAlias(string alias) => _aliases.Contains(alias.Trim());

    public bool ExcludesCharacterName(string surface) => _characterNames.Contains(surface.Trim());
}
