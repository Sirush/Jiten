using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;
using Jiten.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public sealed class DeckMetadataTagPatch
{
    public int TagId { get; init; }
    public int Percentage { get; init; }
}

public sealed class DeckMetadataLinkPatch
{
    public LinkType LinkType { get; init; }
    public string Url { get; init; } = "";
}

/// <summary>A canonical primary edge; the source may be the edited deck or the related one.</summary>
public sealed class DeckMetadataRelationshipPatch
{
    public int SourceDeckId { get; init; }
    public int TargetDeckId { get; init; }
    public DeckRelationshipType RelationshipType { get; init; }
}

/// <summary>
/// Partial deck metadata update. A null member is not part of the patch and leaves the stored value
/// alone; an empty collection clears every row of that kind.
/// </summary>
public sealed class DeckMetadataPatch
{
    public string? OriginalTitle { get; init; }
    public string? RomajiTitle { get; init; }
    public string? EnglishTitle { get; init; }
    public string? Description { get; init; }

    /// <summary>"yyyy-MM-dd"; an empty string clears the date.</summary>
    public string? ReleaseDate { get; init; }

    public MediaType? MediaType { get; init; }
    public List<string>? Aliases { get; init; }

    /// <summary>Final set of character-name surfaces (parser dictionary entries).</summary>
    public List<string>? CharacterNames { get; init; }

    public bool? HideDialoguePercentage { get; init; }
    public bool? HideAverageSentenceLength { get; init; }
    public List<int>? Genres { get; init; }
    public List<DeckMetadataTagPatch>? Tags { get; init; }
    public List<DeckMetadataLinkPatch>? Links { get; init; }
    public List<DeckMetadataRelationshipPatch>? Relationships { get; init; }
}

public sealed class DeckMetadataService(JitenDbContext dbContext)
{
    private const int MaxUrlLength = 2048;

    /// <summary>Loads a deck with every navigation <see cref="ApplyAsync"/> reconciles.</summary>
    public Task<Deck?> LoadForPatchAsync(int deckId, CancellationToken ct = default) =>
        dbContext.Decks
                 .Include(d => d.Links)
                 .Include(d => d.Titles)
                 .Include(d => d.DeckGenres)
                 .Include(d => d.DeckTags)
                 .Include(d => d.DictionaryEntries)
                 .Include(d => d.RelationshipsAsSource)
                 .Include(d => d.RelationshipsAsTarget)
                 .AsSplitQuery()
                 .FirstOrDefaultAsync(d => d.DeckId == deckId, ct);

    public async Task<DeckMetadataSnapshot?> LoadSnapshotAsync(int deckId, CancellationToken ct = default) =>
        (await LoadSnapshotsAsync([deckId], ct)).GetValueOrDefault(deckId);

    /// <summary>Untracked read of the community-editable state, projected to the columns a snapshot shows (tag names, related deck titles).</summary>
    public async Task<Dictionary<int, DeckMetadataSnapshot>> LoadSnapshotsAsync(IReadOnlyCollection<int> deckIds, CancellationToken ct = default)
    {
        if (deckIds.Count == 0)
            return new();

        var rows = await dbContext.Decks.AsNoTracking()
                                  .Where(d => deckIds.Contains(d.DeckId))
                                  .Select(d => new
                                               {
                                                   d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.Description, d.ReleaseDate, d.MediaType,
                                                   Aliases = d.Titles.Where(t => t.TitleType == DeckTitleType.Alias).Select(t => t.Title).ToList(),
                                                   Genres = d.DeckGenres.Select(g => g.Genre).ToList(),
                                                   Tags = d.DeckTags.Select(t => new { t.TagId, t.Tag.Name, t.Percentage }).ToList(),
                                                   Links = d.Links.Select(l => new { l.LinkType, l.Url }).ToList(),
                                                   Outgoing = d.RelationshipsAsSource
                                                               .Select(r => new { r.SourceDeckId, r.TargetDeckId, r.RelationshipType, r.TargetDeck.OriginalTitle })
                                                               .ToList(),
                                                   Incoming = d.RelationshipsAsTarget
                                                               .Select(r => new { r.SourceDeckId, r.TargetDeckId, r.RelationshipType, r.SourceDeck.OriginalTitle })
                                                               .ToList(),
                                                   Names = d.DictionaryEntries.Where(e => e.EntryType == DeckDictionaryEntryType.Name).Select(e => e.Surface).ToList()
                                               })
                                  .AsSplitQuery()
                                  .ToListAsync(ct);

        return rows.ToDictionary(r => r.DeckId, r => DeckMetadataSnapshot.From(
                                     r.OriginalTitle, r.RomajiTitle, r.EnglishTitle, r.Description, r.ReleaseDate, r.MediaType, r.Aliases, r.Genres,
                                     r.Tags.Select(t => new SnapshotTag(t.TagId, t.Name, t.Percentage)),
                                     r.Links.Select(l => new SnapshotLink(l.LinkType, l.Url)),
                                     r.Outgoing.Select(e => new SnapshotRelationship(e.SourceDeckId, e.TargetDeckId, e.RelationshipType, e.TargetDeckId, e.OriginalTitle))
                                      .Concat(r.Incoming.Select(e => new SnapshotRelationship(e.SourceDeckId, e.TargetDeckId, e.RelationshipType, e.SourceDeckId,
                                                                                              e.OriginalTitle))),
                                     r.Names));
    }

    /// <summary>Returns an error or null; person edits lock fields, tombstone removals (not for reverts) and mark tag percentages manual. Caller saves.</summary>
    public async Task<string?> ApplyAsync(Deck deck, DeckMetadataPatch patch, CancellationToken ct = default, string? userId = null, bool recordRemovals = true)
    {
        var error = await ValidateAsync(deck, patch, ct);
        if (error != null)
            return error;

        if (patch.OriginalTitle != null)
            SetLocked(deck, MetadataField.OriginalTitle, deck.OriginalTitle, patch.OriginalTitle.Trim(), v => deck.OriginalTitle = v!);
        if (patch.RomajiTitle != null)
            SetLocked(deck, MetadataField.RomajiTitle, NullIfBlank(deck.RomajiTitle), NullIfBlank(patch.RomajiTitle), v => deck.RomajiTitle = v);
        if (patch.EnglishTitle != null)
            SetLocked(deck, MetadataField.EnglishTitle, NullIfBlank(deck.EnglishTitle), NullIfBlank(patch.EnglishTitle), v => deck.EnglishTitle = v);
        if (patch.Description != null)
            SetLocked(deck, MetadataField.Description, NullIfBlank(deck.Description), NullIfBlank(patch.Description), v => deck.Description = v);
        if (patch.ReleaseDate != null)
            SetLocked(deck, MetadataField.ReleaseDate, deck.ReleaseDate, ParseDate(patch.ReleaseDate)!.Value, v => deck.ReleaseDate = v);
        if (patch.MediaType.HasValue)
            SetLocked(deck, MetadataField.MediaType, deck.MediaType, patch.MediaType.Value, v => deck.MediaType = v);
        if (patch.HideDialoguePercentage.HasValue)
            deck.HideDialoguePercentage = patch.HideDialoguePercentage.Value;
        if (patch.HideAverageSentenceLength.HasValue)
            deck.HideAverageSentenceLength = patch.HideAverageSentenceLength.Value;

        var tombstoned = patch.Genres != null || patch.Tags != null || patch.Aliases != null || patch.CharacterNames != null;
        var exclusions = tombstoned ? await dbContext.DeckMetadataExclusions.Where(x => x.DeckId == deck.DeckId).ToListAsync(ct) : [];

        if (patch.Genres != null)
            ApplyGenres(deck, patch.Genres, exclusions, userId, recordRemovals);
        if (patch.Tags != null)
            ApplyTags(deck, patch.Tags, exclusions, userId, recordRemovals);
        if (patch.Links != null)
            ApplyLinks(deck, patch.Links);
        if (patch.Relationships != null)
            await ApplyRelationshipsAsync(deck, patch.Relationships, ct);
        if (patch.Aliases != null)
            ApplyStringSet(deck, deck.Titles, t => t.TitleType == DeckTitleType.Alias, t => t.Title,
                           alias => new DeckTitle { DeckId = deck.DeckId, Title = alias, TitleType = DeckTitleType.Alias },
                           patch.Aliases, DeckMetadataExclusionKind.Alias, exclusions, userId, recordRemovals);
        if (patch.CharacterNames != null)
            ApplyStringSet(deck, deck.DictionaryEntries, e => e.EntryType == DeckDictionaryEntryType.Name, e => e.Surface,
                           surface => new DeckDictionaryEntry { DeckId = deck.DeckId, Surface = surface, EntryType = DeckDictionaryEntryType.Name },
                           patch.CharacterNames, DeckMetadataExclusionKind.CharacterName, exclusions, userId, recordRemovals);

        deck.LastUpdate = DateTime.UtcNow;
        return null;
    }

    /// <summary>A person's change to a scalar locks it, so provider imports leave it alone.</summary>
    private static void SetLocked<T>(Deck deck, MetadataField field, T current, T value, Action<T> set)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
            return;
        set(value);
        deck.MetadataLocks |= field;
    }

    /// <summary>Parses "yyyy-MM-dd"; blank means "no date" (default), an invalid string returns null.</summary>
    public static DateOnly? ParseDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return default(DateOnly);
        return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", out var date) ? date : null;
    }

    /// <summary>Records a removal so provider imports never re-add it, and lifts the tombstone when a person adds the value back.</summary>
    private void Tombstone(Deck deck, List<DeckMetadataExclusion> exclusions, DeckMetadataExclusionKind kind,
                           IEnumerable<string> removed, IEnumerable<string> added, string? userId, bool recordRemovals)
    {
        var comparer = DeckMetadataExclusionSet.ValueComparer(kind);
        DeckMetadataExclusion? Find(string value) => exclusions.FirstOrDefault(x => x.Kind == kind && comparer.Equals(x.Value.Trim(), value.Trim()));

        foreach (var value in added)
        {
            var existing = Find(value);
            if (existing == null)
                continue;
            dbContext.DeckMetadataExclusions.Remove(existing);
            exclusions.Remove(existing);
        }

        if (!recordRemovals)
            return;

        foreach (var value in removed)
        {
            if (Find(value) != null)
                continue;
            var exclusion = new DeckMetadataExclusion { DeckId = deck.DeckId, Kind = kind, Value = value, CreatedByUserId = userId };
            dbContext.DeckMetadataExclusions.Add(exclusion);
            exclusions.Add(exclusion);
        }
    }

    private void ApplyStringSet<TEntity>(Deck deck, ICollection<TEntity> collection, Func<TEntity, bool> inSet, Func<TEntity, string> key,
                                         Func<string, TEntity> create, List<string> values, DeckMetadataExclusionKind kind,
                                         List<DeckMetadataExclusion> exclusions, string? userId, bool recordRemovals) where TEntity : class
    {
        var target = values.Select(v => v.Trim()).Where(v => v.Length > 0).ToHashSet(StringComparer.Ordinal);
        var current = collection.Where(inSet).ToList();

        var removed = current.Where(e => !target.Contains(key(e))).ToList();
        dbContext.RemoveRange(removed);

        var present = current.Select(key).ToHashSet(StringComparer.Ordinal);
        var added = target.Where(v => !present.Contains(v)).ToList();
        foreach (var value in added)
            collection.Add(create(value));

        Tombstone(deck, exclusions, kind, removed.Select(key), added, userId, recordRemovals);
    }

    /// <summary>Re-reads the patched collections, so callers never serialise stale tracked state.</summary>
    public async Task<DeckMetadataPatchResultDto> BuildResultAsync(int deckId, CancellationToken ct = default)
    {
        var deck = await dbContext.Decks.AsNoTracking()
                                  .Include(d => d.Links)
                                  .Include(d => d.DeckGenres)
                                  .FirstAsync(d => d.DeckId == deckId, ct);

        var tags = await dbContext.DeckTags.AsNoTracking()
                                  .Where(dt => dt.DeckId == deckId)
                                  .Select(dt => new TagWithPercentageDto
                                                {
                                                    TagId = dt.TagId, Name = dt.Tag.Name, Percentage = dt.Percentage
                                                })
                                  .OrderByDescending(t => t.Percentage)
                                  .ToListAsync(ct);

        var edges = await dbContext.DeckRelationships.AsNoTracking()
                                   .Where(r => r.SourceDeckId == deckId || r.TargetDeckId == deckId)
                                   .Include(r => r.SourceDeck)
                                   .Include(r => r.TargetDeck)
                                   .ToListAsync(ct);

        return new DeckMetadataPatchResultDto
               {
                   OriginalTitle = deck.OriginalTitle,
                   RomajiTitle = deck.RomajiTitle ?? "",
                   EnglishTitle = deck.EnglishTitle ?? "",
                   Description = deck.Description ?? "",
                   HideDialoguePercentage = deck.HideDialoguePercentage,
                   HideAverageSentenceLength = deck.HideAverageSentenceLength,
                   Genres = deck.DeckGenres.Select(dg => dg.Genre).OrderBy(g => g.ToString()).ToList(),
                   Tags = tags,
                   Links = deck.Links,
                   Relationships = DeckRelationshipDto.FromDeck(
                       edges.Where(r => r.SourceDeckId == deckId).ToList(),
                       edges.Where(r => r.TargetDeckId == deckId).ToList())
               };
    }

    private async Task<string?> ValidateAsync(Deck deck, DeckMetadataPatch patch, CancellationToken ct)
    {
        if (patch.OriginalTitle != null && string.IsNullOrWhiteSpace(patch.OriginalTitle))
            return "Original title cannot be empty.";

        if (patch.ReleaseDate != null && ParseDate(patch.ReleaseDate) == null)
            return "Release date must be a valid date (yyyy-MM-dd).";

        if (patch.MediaType.HasValue && !Enum.IsDefined(patch.MediaType.Value))
            return $"Unknown media type {(int)patch.MediaType.Value}.";

        if (patch.Description != null && patch.Description.Trim().Length > 2000)
            return "Description must be at most 2000 characters.";

        if (patch.Genres != null)
            foreach (var genre in patch.Genres)
                if (!Enum.IsDefined(typeof(Genre), genre))
                    return $"Unknown genre value {genre}.";

        if (patch.Tags != null)
        {
            foreach (var tag in patch.Tags)
                if (tag.Percentage is < 0 or > 100)
                    return $"Percentage for tag {tag.TagId} must be between 0 and 100.";

            var tagIds = patch.Tags.Select(t => t.TagId).Distinct().ToList();
            if (tagIds.Count > 0)
            {
                var known = await dbContext.Tags.AsNoTracking()
                                          .Where(t => tagIds.Contains(t.TagId))
                                          .Select(t => t.TagId)
                                          .ToListAsync(ct);
                var missing = tagIds.Except(known).ToList();
                if (missing.Count > 0)
                    return $"Unknown tag id {missing[0]}.";
            }
        }

        if (patch.Links != null)
            foreach (var link in patch.Links)
            {
                if (!Enum.IsDefined(typeof(LinkType), link.LinkType))
                    return $"Unknown link type {(int)link.LinkType}.";

                var url = link.Url.Trim();
                if (url.Length == 0)
                    return "Link URL cannot be empty.";
                if (url.Length > MaxUrlLength)
                    return $"Link URL must be at most {MaxUrlLength} characters.";
                // The URL is rendered as an href on the deck card, so anything but http(s) is a script vector.
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return $"Link URL must start with http:// or https:// ({url}).";
            }

        if (patch.Relationships != null)
        {
            foreach (var edge in patch.Relationships)
            {
                if (DeckRelationship.ValidateEdge(edge.SourceDeckId, edge.TargetDeckId, edge.RelationshipType) is { } edgeError)
                    return edgeError;
                if (edge.SourceDeckId != deck.DeckId && edge.TargetDeckId != deck.DeckId)
                    return "Every relationship must have this deck as one of its endpoints.";
            }

            var otherIds = patch.Relationships
                                .Select(r => r.SourceDeckId == deck.DeckId ? r.TargetDeckId : r.SourceDeckId)
                                .Distinct()
                                .ToList();
            if (otherIds.Count > 0)
            {
                var known = await dbContext.Decks.AsNoTracking()
                                          .Where(d => otherIds.Contains(d.DeckId))
                                          .Select(d => d.DeckId)
                                          .ToListAsync(ct);
                var missing = otherIds.Except(known).ToList();
                if (missing.Count > 0)
                    return $"Unknown related deck id {missing[0]}.";
            }
        }

        return null;
    }

    private void ApplyGenres(Deck deck, List<int> genres, List<DeckMetadataExclusion> exclusions, string? userId, bool recordRemovals)
    {
        var target = genres.Select(g => (Genre)g).ToHashSet();

        var removed = deck.DeckGenres.Where(dg => !target.Contains(dg.Genre)).ToList();
        dbContext.RemoveRange(removed);

        var present = deck.DeckGenres.Select(dg => dg.Genre).ToHashSet();
        var added = target.Where(g => !present.Contains(g)).ToList();
        foreach (var genre in added)
            deck.DeckGenres.Add(new DeckGenre { DeckId = deck.DeckId, Genre = genre });

        Tombstone(deck, exclusions, DeckMetadataExclusionKind.Genre,
                  removed.Select(g => ((int)g.Genre).ToString()), added.Select(g => ((int)g).ToString()), userId, recordRemovals);
    }

    private void ApplyTags(Deck deck, List<DeckMetadataTagPatch> tags, List<DeckMetadataExclusion> exclusions, string? userId, bool recordRemovals)
    {
        var target = tags.GroupBy(t => t.TagId)
                         .ToDictionary(g => g.Key, g => (byte)g.Last().Percentage);

        var removed = deck.DeckTags.Where(dt => !target.ContainsKey(dt.TagId)).ToList();
        dbContext.RemoveRange(removed);

        var added = new List<int>();
        foreach (var (tagId, percentage) in target)
        {
            var existing = deck.DeckTags.FirstOrDefault(dt => dt.TagId == tagId);
            if (existing != null)
            {
                if (existing.Percentage != percentage)
                {
                    existing.Percentage = percentage;
                    existing.IsManual = true;
                }
            }
            else
            {
                deck.DeckTags.Add(new DeckTag { DeckId = deck.DeckId, TagId = tagId, Percentage = percentage, IsManual = true });
                added.Add(tagId);
            }
        }

        Tombstone(deck, exclusions, DeckMetadataExclusionKind.Tag,
                  removed.Select(t => t.TagId.ToString()), added.Select(t => t.ToString()), userId, recordRemovals);
    }

    private void ApplyLinks(Deck deck, List<DeckMetadataLinkPatch> links)
    {
        var target = links.Select(l => (l.LinkType, Url: l.Url.Trim())).Distinct().ToList();
        var targetKeys = target.ToHashSet();

        dbContext.RemoveRange(deck.Links.Where(l => !targetKeys.Contains((l.LinkType, l.Url))).ToList());

        var present = deck.Links.Select(l => (l.LinkType, l.Url)).ToHashSet();
        foreach (var (linkType, url) in target)
            if (!present.Contains((linkType, url)))
                deck.Links.Add(new Link { DeckId = deck.DeckId, LinkType = linkType, Url = url });
    }

    /// <summary>
    /// Edges are stored canonically and may have this deck on either side, so both directions are
    /// reconciled against a payload that carries every edge touching the deck.
    /// </summary>
    private async Task ApplyRelationshipsAsync(Deck deck, List<DeckMetadataRelationshipPatch> relationships, CancellationToken ct)
    {
        var target = relationships
                     .Select(r => (r.SourceDeckId, r.TargetDeckId, r.RelationshipType))
                     .ToHashSet();

        // Legacy group rows wait for the series conversion; editors no longer send them, so they must not read as removals.
        var existingEdges = deck.RelationshipsAsSource.Concat(deck.RelationshipsAsTarget)
                                .Where(e => !DeckRelationship.IsLegacyGroupType(e.RelationshipType))
                                .ToList();

        var removed = existingEdges.Where(e => !target.Contains((e.SourceDeckId, e.TargetDeckId, e.RelationshipType))).ToList();
        dbContext.DeckRelationships.RemoveRange(removed);

        var present = existingEdges
                      .Select(e => (e.SourceDeckId, e.TargetDeckId, e.RelationshipType))
                      .ToHashSet();

        var added = target.Where(edge => !present.Contains(edge)).ToList();
        foreach (var edge in added)
            dbContext.DeckRelationships.Add(new DeckRelationship
                                            {
                                                SourceDeckId = edge.SourceDeckId,
                                                TargetDeckId = edge.TargetDeckId,
                                                RelationshipType = edge.RelationshipType
                                            });

        await RelationshipOverrides.MarkAsync(dbContext, removed.Select(e => (e.SourceDeckId, e.TargetDeckId))
                                                                .Concat(added.Select(e => (e.SourceDeckId, e.TargetDeckId))), ct);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
