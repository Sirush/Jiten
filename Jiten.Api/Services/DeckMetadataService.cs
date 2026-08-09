using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
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
                 .Include(d => d.DeckGenres)
                 .Include(d => d.DeckTags)
                 .Include(d => d.RelationshipsAsSource)
                 .Include(d => d.RelationshipsAsTarget)
                 .FirstOrDefaultAsync(d => d.DeckId == deckId, ct);

    /// <summary>
    /// Applies the patch to a tracked deck and returns an error message, or null when it applied.
    /// The caller owns SaveChanges so the write can join a larger transaction.
    /// </summary>
    public async Task<string?> ApplyAsync(Deck deck, DeckMetadataPatch patch, CancellationToken ct = default)
    {
        var error = await ValidateAsync(deck, patch, ct);
        if (error != null)
            return error;

        if (patch.OriginalTitle != null)
            deck.OriginalTitle = patch.OriginalTitle.Trim();
        if (patch.RomajiTitle != null)
            deck.RomajiTitle = NullIfBlank(patch.RomajiTitle);
        if (patch.EnglishTitle != null)
            deck.EnglishTitle = NullIfBlank(patch.EnglishTitle);
        if (patch.Description != null)
            deck.Description = NullIfBlank(patch.Description);
        if (patch.HideDialoguePercentage.HasValue)
            deck.HideDialoguePercentage = patch.HideDialoguePercentage.Value;
        if (patch.HideAverageSentenceLength.HasValue)
            deck.HideAverageSentenceLength = patch.HideAverageSentenceLength.Value;

        if (patch.Genres != null)
            ApplyGenres(deck, patch.Genres);
        if (patch.Tags != null)
            ApplyTags(deck, patch.Tags);
        if (patch.Links != null)
            ApplyLinks(deck, patch.Links);
        if (patch.Relationships != null)
            ApplyRelationships(deck, patch.Relationships);

        deck.LastUpdate = DateTime.UtcNow;
        return null;
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
                if (!Enum.IsDefined(typeof(DeckRelationshipType), edge.RelationshipType) ||
                    !DeckRelationship.IsPrimaryRelationship(edge.RelationshipType))
                    return $"Relationship type {(int)edge.RelationshipType} is not a primary relationship.";
                if (edge.SourceDeckId == edge.TargetDeckId)
                    return "A deck cannot be related to itself.";
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

    private void ApplyGenres(Deck deck, List<int> genres)
    {
        var target = genres.Select(g => (Genre)g).ToHashSet();

        dbContext.RemoveRange(deck.DeckGenres.Where(dg => !target.Contains(dg.Genre)).ToList());

        var present = deck.DeckGenres.Select(dg => dg.Genre).ToHashSet();
        foreach (var genre in target)
            if (!present.Contains(genre))
                deck.DeckGenres.Add(new DeckGenre { DeckId = deck.DeckId, Genre = genre });
    }

    private void ApplyTags(Deck deck, List<DeckMetadataTagPatch> tags)
    {
        var target = tags.GroupBy(t => t.TagId)
                         .ToDictionary(g => g.Key, g => (byte)g.Last().Percentage);

        dbContext.RemoveRange(deck.DeckTags.Where(dt => !target.ContainsKey(dt.TagId)).ToList());

        foreach (var (tagId, percentage) in target)
        {
            var existing = deck.DeckTags.FirstOrDefault(dt => dt.TagId == tagId);
            if (existing != null)
                existing.Percentage = percentage;
            else
                deck.DeckTags.Add(new DeckTag { DeckId = deck.DeckId, TagId = tagId, Percentage = percentage });
        }
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
    private void ApplyRelationships(Deck deck, List<DeckMetadataRelationshipPatch> relationships)
    {
        var target = relationships
                     .Select(r => (r.SourceDeckId, r.TargetDeckId, r.RelationshipType))
                     .ToHashSet();

        var existingEdges = deck.RelationshipsAsSource.Concat(deck.RelationshipsAsTarget).ToList();

        foreach (var existing in existingEdges)
            if (!target.Contains((existing.SourceDeckId, existing.TargetDeckId, existing.RelationshipType)))
                dbContext.DeckRelationships.Remove(existing);

        var present = existingEdges
                      .Select(e => (e.SourceDeckId, e.TargetDeckId, e.RelationshipType))
                      .ToHashSet();

        foreach (var edge in target)
            if (!present.Contains(edge))
                dbContext.DeckRelationships.Add(new DeckRelationship
                                                {
                                                    SourceDeckId = edge.SourceDeckId,
                                                    TargetDeckId = edge.TargetDeckId,
                                                    RelationshipType = edge.RelationshipType
                                                });
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
