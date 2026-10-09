using System.Text.Json;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public interface IStudyDeckWordSources
{
    /// <summary>Source of each media or media group study deck, keyed by UserStudyDeckId; a deck whose media deck or group is gone is left out.</summary>
    Task<Dictionary<int, DeckWordSource>> ResolveAsync(IReadOnlyCollection<UserStudyDeck> studyDecks);

    Task<DeckWordSource?> ResolveAsync(UserStudyDeck studyDeck);
}

public sealed class StudyDeckWordSources(JitenDbContext context, MediaGroupService mediaGroupService) : IStudyDeckWordSources
{
    public static MediaGroupRef? GroupOf(UserStudyDeck studyDeck) =>
        studyDeck is { DeckType: StudyDeckType.MediaGroup, GroupKind: { } kind, GroupId: { } id } ? new MediaGroupRef(kind, id) : null;

    public async Task<Dictionary<int, DeckWordSource>> ResolveAsync(IReadOnlyCollection<UserStudyDeck> studyDecks)
    {
        var result = new Dictionary<int, DeckWordSource>();

        var mediaDecks = studyDecks.Where(sd => sd.DeckType == StudyDeckType.MediaDeck && sd.DeckId.HasValue).ToList();
        if (mediaDecks.Count > 0)
        {
            var mediaDeckIds = mediaDecks.Select(sd => sd.DeckId!.Value).Distinct().ToList();
            var decks = await context.Decks.AsNoTracking()
                                     .Where(d => mediaDeckIds.Contains(d.DeckId))
                                     .Select(d => new { d.DeckId, d.WordCount, d.OriginalTitle })
                                     .ToDictionaryAsync(d => d.DeckId);

            foreach (var sd in mediaDecks)
                if (decks.TryGetValue(sd.DeckId!.Value, out var deck))
                    result[sd.UserStudyDeckId] = DeckWordSource.ForDeck(deck.DeckId, deck.WordCount, deck.OriginalTitle);
        }

        var groupDecks = studyDecks.Select(sd => (StudyDeck: sd, Group: GroupOf(sd))).Where(x => x.Group != null).ToList();
        if (groupDecks.Count > 0)
        {
            var groups = await mediaGroupService.DescribeManyAsync(groupDecks.Select(x => x.Group!.Value));
            var resolved = groupDecks.Where(x => groups.ContainsKey(x.Group!.Value)).ToList();
            var narrowed = await mediaGroupService.NarrowAsync(
                resolved.Select(x => (groups[x.Group!.Value].DeckIds,
                                      new MediaGroupFilter(StudyDeckGroupFilter.ParseMediaTypes(x.StudyDeck.GroupMediaTypes),
                                                           StudyDeckGroupFilter.ParseIds(x.StudyDeck.GroupExcludedDeckIds))))
                        .ToList());

            for (var i = 0; i < resolved.Count; i++)
                result[resolved[i].StudyDeck.UserStudyDeckId] = DeckWordSource.Merged(narrowed[i], groups[resolved[i].Group!.Value]);
        }

        return result;
    }

    public async Task<DeckWordSource?> ResolveAsync(UserStudyDeck studyDeck) =>
        (await ResolveAsync([studyDeck])).GetValueOrDefault(studyDeck.UserStudyDeckId);
}

/// <summary>JSON int[] columns of a media group study deck; null or empty means no narrowing.</summary>
public static class StudyDeckGroupFilter
{
    public static List<int>? ParseIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<List<int>>(json) is { Count: > 0 } ids ? ids : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static List<MediaType>? ParseMediaTypes(string? json) =>
        ParseIds(json)?.Select(v => (MediaType)v).ToList();

    public static string? Serialize(IEnumerable<int>? values)
    {
        var normalised = values?.Distinct().Order().ToList();
        return normalised is { Count: > 0 } ? JsonSerializer.Serialize(normalised) : null;
    }
}
