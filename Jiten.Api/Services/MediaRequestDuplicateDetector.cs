using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Providers;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

/// <summary>
/// Runs once a request's provider metadata has landed, when its aliases and cross-provider links are known. A match is an
/// identical normalised title or a link to the same catalogue entry, within the same media type; fuzzy matches stay form hints.
/// </summary>
public class MediaRequestDuplicateDetector(JitenDbContext context)
{
    private const int CandidatesPerName = 10;
    private const int MaxNamesSearched = 12;
    private const int MaxDeckCandidatesPerName = 5;

    /// <summary>
    /// Points the newer request of each matching pair at the oldest one, so a pair is flagged once whichever lookup finishes
    /// first, and points the request at a deck that already holds the media. The caller saves.
    /// </summary>
    public async Task DetectAsync(MediaRequest request)
    {
        if (request.Kind != MediaRequestKind.New
            || request.Status is not (MediaRequestStatus.Open or MediaRequestStatus.InProgress))
            return;

        var allNames = new[] { request.Title }.Concat(request.Titles.Select(t => t.Title)).ToList();
        var searchedNames = allNames.Where(n => n.Trim().Length >= MediaDuplicateService.MinTitleLength)
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .Take(MaxNamesSearched)
                                    .ToList();
        var nameKeys = TitleNormalizer.DistinctiveKeys(allNames);
        var urls = new[] { request.ExternalUrl }.Concat(request.Links.Select(l => l.Url)).ToList();
        var linkKeys = EntityKeys(urls);

        if (nameKeys.Count == 0 && linkKeys.Count == 0)
            return;

        var titleSearch = new MediaTitleSearchService(context);
        var duplicateFinder = new MediaDuplicateService(context, titleSearch);

        await FlagRequestsAsync(request, searchedNames, urls, nameKeys, linkKeys, titleSearch, duplicateFinder);
        await FlagDeckAsync(request, searchedNames, urls, nameKeys, titleSearch, duplicateFinder);
    }

    private async Task FlagRequestsAsync(MediaRequest request, List<string> searchedNames, List<string?> urls, HashSet<string> nameKeys,
        HashSet<ExternalEntityKey> linkKeys, MediaTitleSearchService titleSearch, MediaDuplicateService duplicateFinder)
    {
        var candidateIds = (await duplicateFinder.FindRequests(null, urls, CandidatesPerName, request.MediaType, fulfillableOnly: true))
                           .Select(r => r.Id)
                           .ToHashSet();

        foreach (var name in searchedNames)
            candidateIds.UnionWith(await titleSearch.SearchActiveRequests(name, CandidatesPerName, 0, request.MediaType, fulfillableOnly: true));

        candidateIds.Remove(request.Id);

        var candidates = candidateIds.Count == 0
            ? []
            : await context.MediaRequests
                           .Include(r => r.Titles)
                           .Include(r => r.Links)
                           .Where(r => candidateIds.Contains(r.Id))
                           .ToListAsync();

        var confirmed = candidates.Where(c => IsSameMedia(c, nameKeys, linkKeys)).ToList();
        var oldest = confirmed.Where(c => c.Id < request.Id).MinBy(c => c.Id);

        if (!request.DuplicateFlagDismissed)
            request.PossibleDuplicateOfRequestId = oldest?.Id;

        var target = oldest?.Id ?? request.Id;
        foreach (var newer in confirmed.Where(c => c.Id > request.Id && !c.DuplicateFlagDismissed && c.PossibleDuplicateOfRequestId == null))
            newer.PossibleDuplicateOfRequestId = target;
    }

    private async Task FlagDeckAsync(MediaRequest request, List<string> searchedNames, List<string?> urls, HashSet<string> nameKeys,
        MediaTitleSearchService titleSearch, MediaDuplicateService duplicateFinder)
    {
        if (request.DuplicateFlagDismissed)
            return;

        var linkedDeckIds = (await duplicateFinder.FindDecks(null, urls, MaxDeckCandidatesPerName, request.MediaType))
                            .Select(d => d.DeckId)
                            .ToList();

        var titleDeckIds = new HashSet<int>();
        foreach (var name in searchedNames)
            titleDeckIds.UnionWith((await titleSearch.SearchPrimaryDecks(name, MaxDeckCandidatesPerName, 0, request.MediaType)).Select(h => h.DeckId));

        var titleMatchedDeckIds = new List<int>();
        if (titleDeckIds.Count > 0 && nameKeys.Count > 0)
        {
            var deckTitles = await context.DeckTitles.AsNoTracking()
                                          .Where(t => titleDeckIds.Contains(t.DeckId))
                                          .Select(t => new { t.DeckId, t.Title })
                                          .ToListAsync();

            titleMatchedDeckIds = deckTitles.Where(t => nameKeys.Contains(TitleNormalizer.Normalize(t.Title)))
                                            .Select(t => t.DeckId)
                                            .Distinct()
                                            .Order()
                                            .ToList();
        }

        request.PossibleDuplicateDeckId = linkedDeckIds.Concat(titleMatchedDeckIds).Cast<int?>().FirstOrDefault();
    }

    private static bool IsSameMedia(MediaRequest candidate, HashSet<string> nameKeys, HashSet<ExternalEntityKey> linkKeys)
    {
        var candidateLinks = EntityKeys(new[] { candidate.ExternalUrl }.Concat(candidate.Links.Select(l => l.Url)));
        if (candidateLinks.Overlaps(linkKeys))
            return true;

        var candidateNames = TitleNormalizer.DistinctiveKeys(new[] { candidate.Title }.Concat(candidate.Titles.Select(t => t.Title)));
        return candidateNames.Overlaps(nameKeys);
    }

    private static HashSet<ExternalEntityKey> EntityKeys(IEnumerable<string?> urls) =>
        urls.Select(url => ExternalUrlParser.TryGetEntityKey(url, out var key) ? key : (ExternalEntityKey?)null)
            .OfType<ExternalEntityKey>()
            .ToHashSet();
}
