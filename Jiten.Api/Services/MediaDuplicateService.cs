using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Providers;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public static class MediaRequestQueryExtensions
{
    public static IQueryable<MediaRequest> WhereActive(this IQueryable<MediaRequest> query, MediaType? mediaType, bool fulfillableOnly)
    {
        query = query.Where(r => r.Status == MediaRequestStatus.Open || r.Status == MediaRequestStatus.InProgress);

        if (mediaType.HasValue)
            query = query.Where(r => r.MediaType == mediaType.Value);

        if (fulfillableOnly)
            query = query.Where(r => r.Kind == MediaRequestKind.New && r.FulfilledDeckId == null);

        return query;
    }
}

/// <summary>
/// Finds decks and active requests that describe the same media as a typed title or a set of external links.
/// A link to the same catalogue entry, or an identical title, is an exact match; everything else is a fuzzy hint.
/// </summary>
public class MediaDuplicateService(JitenDbContext context, MediaTitleSearchService titleSearch)
{
    public const int MinTitleLength = 2;

    public async Task<List<DuplicateCheckDeckDto>> FindDecks(string? title, IEnumerable<string?> urls, int limit, MediaType? mediaType = null)
    {
        var trimmedTitle = title?.Trim() ?? string.Empty;
        var linkDeckIds = new List<int>();
        foreach (var key in ParseKeys(urls))
            linkDeckIds.AddRange(await DeckIdsByLink(key, mediaType));

        var titleDeckIds = trimmedTitle.Length >= MinTitleLength
            ? (await titleSearch.SearchPrimaryDecks(trimmedTitle, limit, MediaTitleSearchService.DuplicateLevenshteinSlack, mediaType))
              .Select(h => h.DeckId).ToList()
            : [];

        var orderedIds = linkDeckIds.Concat(titleDeckIds).Distinct().ToList();
        if (orderedIds.Count == 0)
            return [];

        var decks = await context.Decks.AsNoTracking()
                                 .Where(d => orderedIds.Contains(d.DeckId))
                                 .Select(d => new DuplicateCheckDeckDto
                                 {
                                     DeckId = d.DeckId,
                                     Title = d.OriginalTitle,
                                     RomajiTitle = d.RomajiTitle,
                                     EnglishTitle = d.EnglishTitle,
                                     MediaType = d.MediaType
                                 })
                                 .ToDictionaryAsync(d => d.DeckId);

        var exactTitleDeckIds = trimmedTitle.Length >= MinTitleLength
            ? (await context.DeckTitles.AsNoTracking()
                            .Where(dt => titleDeckIds.Contains(dt.DeckId))
                            .Select(dt => new { dt.DeckId, dt.Title })
                            .ToListAsync())
              .Where(dt => IsSameTitle(dt.Title, trimmedTitle))
              .Select(dt => dt.DeckId)
              .ToHashSet()
            : [];

        var linkIdSet = linkDeckIds.ToHashSet();
        return orderedIds.Where(decks.ContainsKey)
                         .Select(id =>
                         {
                             var deck = decks[id];
                             deck.IsExactMatch = linkIdSet.Contains(id) || exactTitleDeckIds.Contains(id);
                             return deck;
                         })
                         .OrderByDescending(d => d.IsExactMatch)
                         .Take(limit)
                         .ToList();
    }

    public async Task<List<DuplicateCheckRequestDto>> FindRequests(string? title, IEnumerable<string?> urls, int limit,
        MediaType? mediaType = null, bool fulfillableOnly = false)
    {
        var trimmedTitle = title?.Trim() ?? string.Empty;
        var linkRequestIds = new List<int>();
        foreach (var key in ParseKeys(urls))
            linkRequestIds.AddRange(await RequestIdsByLink(key, mediaType, fulfillableOnly));

        var titleRequestIds = trimmedTitle.Length >= MinTitleLength
            ? await titleSearch.SearchActiveRequests(trimmedTitle, limit, MediaTitleSearchService.DuplicateLevenshteinSlack, mediaType,
                fulfillableOnly)
            : [];

        var orderedIds = linkRequestIds.Concat(titleRequestIds).Distinct().ToList();
        if (orderedIds.Count == 0)
            return [];

        var requests = await context.MediaRequests.AsNoTracking()
                                    .Where(r => orderedIds.Contains(r.Id))
                                    .Select(r => new DuplicateCheckRequestDto
                                    {
                                        Id = r.Id,
                                        Title = r.Title,
                                        MediaType = r.MediaType,
                                        Status = r.Status,
                                        UpvoteCount = r.UpvoteCount
                                    })
                                    .ToDictionaryAsync(r => r.Id);

        var linkIdSet = linkRequestIds.ToHashSet();
        return orderedIds.Where(requests.ContainsKey)
                         .Select(id =>
                         {
                             var request = requests[id];
                             request.IsExactMatch = linkIdSet.Contains(id) || IsSameTitle(request.Title, trimmedTitle);
                             return request;
                         })
                         .OrderByDescending(r => r.IsExactMatch)
                         .Take(limit)
                         .ToList();
    }

    private static bool IsSameTitle(string candidate, string title) =>
        title.Length > 0 && string.Equals(candidate.Trim(), title, StringComparison.OrdinalIgnoreCase);

    private static List<ExternalEntityKey> ParseKeys(IEnumerable<string?> urls) =>
        urls.Select(url => ExternalUrlParser.TryGetEntityKey(url, out var key) ? key : (ExternalEntityKey?)null)
            .OfType<ExternalEntityKey>()
            .Distinct()
            .ToList();

    private async Task<List<int>> DeckIdsByLink(ExternalEntityKey key, MediaType? mediaType)
    {
        // The substring filter only narrows the scan; the parsed key decides, so "v17" never matches "v170".
        var token = key.Token.ToLowerInvariant();
        var query = context.Set<Link>().AsNoTracking()
                           .Where(l => l.LinkType == key.LinkType && l.Deck.ParentDeckId == null && l.Url.ToLower().Contains(token));

        if (mediaType.HasValue)
            query = query.Where(l => l.Deck.MediaType == mediaType.Value);

        var candidates = await query.Select(l => new { l.DeckId, l.Url }).ToListAsync();
        return candidates.Where(c => ExternalUrlParser.TryGetEntityKey(c.Url, out var other) && other == key)
                         .Select(c => c.DeckId)
                         .Distinct()
                         .ToList();
    }

    private async Task<List<int>> RequestIdsByLink(ExternalEntityKey key, MediaType? mediaType, bool fulfillableOnly)
    {
        var token = key.Token.ToLowerInvariant();
        var candidates = await context.MediaRequests.AsNoTracking()
                                      .Where(r => r.ExternalUrl != null && r.ExternalUrl.ToLower().Contains(token))
                                      .WhereActive(mediaType, fulfillableOnly)
                                      .OrderByDescending(r => r.UpvoteCount)
                                      .Select(r => new { r.Id, r.ExternalUrl })
                                      .ToListAsync();

        return candidates.Where(c => ExternalUrlParser.TryGetEntityKey(c.ExternalUrl, out var other) && other == key)
                         .Select(c => c.Id)
                         .ToList();
    }
}
