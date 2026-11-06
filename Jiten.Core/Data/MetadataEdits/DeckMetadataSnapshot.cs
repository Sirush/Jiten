using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.Data.MetadataEdits;

public sealed record SnapshotTag(int TagId, string Name, int Percentage);

public sealed record SnapshotLink(LinkType LinkType, string Url);

public sealed record SnapshotRelationship(int SourceDeckId, int TargetDeckId, DeckRelationshipType RelationshipType, int OtherDeckId, string OtherDeckTitle);

/// <summary>A null id is a franchise still to be started with <paramref name="WithDeckId"/>, whose titles it carries; applying it records the new id.</summary>
public sealed record SnapshotFranchise(int? FranchiseId, string OriginalTitle, string? RomajiTitle, string? EnglishTitle, int? WithDeckId = null);

/// <summary>The community-editable state of one deck, in the JSON forms stored in <see cref="MetadataFieldChange"/>.</summary>
public sealed class DeckMetadataSnapshot
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string OriginalTitle { get; init; } = "";
    public string? RomajiTitle { get; init; }
    public string? EnglishTitle { get; init; }
    public string? Description { get; init; }
    public string? ReleaseDate { get; init; }
    public MediaType MediaType { get; init; }
    public List<string> Aliases { get; init; } = [];
    public List<int> Genres { get; init; } = [];
    public List<SnapshotTag> Tags { get; init; } = [];
    public List<SnapshotLink> Links { get; init; } = [];
    public List<SnapshotRelationship> Relationships { get; init; } = [];
    public List<string> CharacterNames { get; init; } = [];

    /// <summary>The saved franchise board holding the deck, not its derived franchise; null when only links or series place it.</summary>
    public SnapshotFranchise? Franchise { get; init; }

    /// <summary>Unloaded navigations read as empty, so load the deck through <see cref="DeckMetadataSnapshotQueries.IncludeSnapshotGraph"/>; Franchise stays null because provider imports never write it.</summary>
    public static DeckMetadataSnapshot Capture(Deck deck) =>
        From(deck.OriginalTitle, deck.RomajiTitle, deck.EnglishTitle, deck.Description, deck.ReleaseDate, deck.MediaType,
             deck.Titles.Where(t => t.TitleType == DeckTitleType.Alias).Select(t => t.Title),
             deck.DeckGenres.Select(g => g.Genre),
             deck.DeckTags.Select(t => new SnapshotTag(t.TagId, t.Tag?.Name ?? "", t.Percentage)),
             deck.Links.Select(l => new SnapshotLink(l.LinkType, l.Url)),
             deck.RelationshipsAsSource.Select(r => new SnapshotRelationship(r.SourceDeckId, r.TargetDeckId, r.RelationshipType, r.TargetDeckId,
                                                                             r.TargetDeck?.OriginalTitle ?? ""))
                 .Concat(deck.RelationshipsAsTarget.Select(r => new SnapshotRelationship(r.SourceDeckId, r.TargetDeckId, r.RelationshipType,
                                                                                         r.SourceDeckId, r.SourceDeck?.OriginalTitle ?? ""))),
             deck.DictionaryEntries.Where(e => e.EntryType == DeckDictionaryEntryType.Name).Select(e => e.Surface));

    /// <summary>Canonical form shared by tracked captures and projected loads: blanks as null, lists deduplicated and ordered, legacy group edges dropped.</summary>
    public static DeckMetadataSnapshot From(string originalTitle, string? romajiTitle, string? englishTitle, string? description, DateOnly releaseDate,
                                            MediaType mediaType, IEnumerable<string> aliases, IEnumerable<Genre> genres, IEnumerable<SnapshotTag> tags,
                                            IEnumerable<SnapshotLink> links, IEnumerable<SnapshotRelationship> relationships,
                                            IEnumerable<string> characterNames, SnapshotFranchise? franchise = null) => new()
    {
        OriginalTitle = originalTitle,
        RomajiTitle = NullIfBlank(romajiTitle),
        EnglishTitle = NullIfBlank(englishTitle),
        Description = NullIfBlank(description),
        ReleaseDate = FormatDate(releaseDate),
        MediaType = mediaType,
        Aliases = aliases.Distinct().Order(StringComparer.Ordinal).ToList(),
        Genres = genres.Select(g => (int)g).Distinct().Order().ToList(),
        Tags = tags.OrderBy(t => t.TagId).ToList(),
        Links = links.OrderBy(l => l.LinkType).ThenBy(l => l.Url, StringComparer.Ordinal).ToList(),
        Relationships = relationships.Where(r => !DeckRelationship.IsLegacyGroupType(r.RelationshipType))
                                     .OrderBy(r => r.SourceDeckId).ThenBy(r => r.TargetDeckId).ThenBy(r => r.RelationshipType)
                                     .ToList(),
        CharacterNames = characterNames.Distinct().Order(StringComparer.Ordinal).ToList(),
        Franchise = franchise
    };

    public JsonElement Value(MetadataField field) => field switch
    {
        MetadataField.OriginalTitle => ToElement(OriginalTitle),
        MetadataField.RomajiTitle => ToElement(RomajiTitle),
        MetadataField.EnglishTitle => ToElement(EnglishTitle),
        MetadataField.Description => ToElement(Description),
        MetadataField.ReleaseDate => ToElement(ReleaseDate),
        MetadataField.MediaType => ToElement((int)MediaType),
        MetadataField.Aliases => ToElement(Aliases),
        MetadataField.Genres => ToElement(Genres),
        MetadataField.Tags => ToElement(Tags),
        MetadataField.Links => ToElement(Links),
        MetadataField.Relationships => ToElement(Relationships),
        MetadataField.CharacterNames => ToElement(CharacterNames),
        MetadataField.Franchise => ToElement(Franchise),
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
    };

    /// <summary>Changes between two snapshots, one per differing field, compared on identity keys only (tag names and related titles are display data).</summary>
    public static List<MetadataFieldChange> Diff(DeckMetadataSnapshot before, DeckMetadataSnapshot after)
    {
        var changes = new List<MetadataFieldChange>();
        foreach (var field in MetadataFields.All)
        {
            var b = before.Value(field);
            var a = after.Value(field);
            if (!KeyEquals(field, b, a))
                changes.Add(new MetadataFieldChange { Field = field, Before = b, After = a });
        }

        return changes;
    }

    public static bool KeyEquals(MetadataField field, JsonElement a, JsonElement b) => Key(field, a) == Key(field, b);

    /// <summary>Canonical comparison string for a field value.</summary>
    public static string Key(MetadataField field, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return "null";

        switch (field)
        {
            case MetadataField.Tags:
                return string.Join(";", value.EnumerateArray()
                                             .Select(t => $"{t.GetProperty("tagId").GetInt32()}:{t.GetProperty("percentage").GetInt32()}")
                                             .Order(StringComparer.Ordinal));
            case MetadataField.Relationships:
                return string.Join(";", value.EnumerateArray()
                                             .Select(r => $"{r.GetProperty("sourceDeckId").GetInt32()}>{r.GetProperty("targetDeckId").GetInt32()}:{r.GetProperty("relationshipType").GetInt32()}")
                                             .Order(StringComparer.Ordinal));
            case MetadataField.Links:
                return string.Join(";", value.EnumerateArray()
                                             .Select(l => $"{l.GetProperty("linkType").GetInt32()}|{l.GetProperty("url").GetString()}")
                                             .Order(StringComparer.Ordinal));
            case MetadataField.Aliases or MetadataField.CharacterNames:
                return string.Join("\u0001", value.EnumerateArray().Select(s => s.GetString() ?? "").Order(StringComparer.Ordinal));
            case MetadataField.Franchise:
                return value.GetProperty("franchiseId") is { ValueKind: JsonValueKind.Number } id
                    ? id.GetInt32().ToString()
                    : "with:" + value.GetProperty("withDeckId").GetInt32();
            case MetadataField.Genres:
                return string.Join(",", value.EnumerateArray().Select(g => g.GetInt32()).Order());
            case MetadataField.Description or MetadataField.RomajiTitle or MetadataField.EnglishTitle or MetadataField.OriginalTitle:
                return value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()) ? "null" : StringKey(value);
            default:
                return StringKey(value);
        }
    }

    /// <summary>Compares strings by value, never raw JSON: jsonb returns literal UTF-8 while the serializer escapes non-ASCII as \uXXXX.</summary>
    private static string StringKey(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? "s:" + value.GetString() : value.GetRawText();

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Json);

    public static string? FormatDate(DateOnly date) => date == default ? null : date.ToString("yyyy-MM-dd");

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

public static class DeckMetadataSnapshotQueries
{
    /// <summary>Every navigation <see cref="DeckMetadataSnapshot.Capture"/> reads, for tracked loads that also write the deck.</summary>
    public static IQueryable<Deck> IncludeSnapshotGraph(this IQueryable<Deck> decks) =>
        decks.Include(d => d.Links)
             .Include(d => d.Titles)
             .Include(d => d.DeckGenres)
             .Include(d => d.DeckTags).ThenInclude(t => t.Tag)
             .Include(d => d.DictionaryEntries)
             .Include(d => d.RelationshipsAsSource).ThenInclude(r => r.TargetDeck)
             .Include(d => d.RelationshipsAsTarget).ThenInclude(r => r.SourceDeck)
             .AsSplitQuery();
}
