using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.WebNovel;

public static class WebNovelDeckWork
{
    /// <summary>
    /// The ledger is the authority on which work a deck came from; a work link is the fallback for a webnovel
    /// deck that was added by hand rather than imported. Null when the deck has neither.
    /// </summary>
    public static WebNovelWorkRef? Resolve(WebNovelWorkRef? tracked, IEnumerable<Link> links) =>
        tracked ?? WebNovelUrlParser.FirstWorkLink(links);

    public static async Task<WebNovelWorkRef?> ResolveAsync(JitenDbContext context, int deckId, IEnumerable<Link> links) =>
        Resolve(await context.WebNovelSources
                             .Where(s => s.DeckId == deckId)
                             .Select(s => new WebNovelWorkRef(s.Provider, s.SourceId))
                             .FirstOrDefaultAsync(),
                links);

    public static Task<Dictionary<int, WebNovelWorkRef>> TrackedByDeckAsync(JitenDbContext context) =>
        context.WebNovelSources
               .Select(s => new { s.DeckId, Work = new WebNovelWorkRef(s.Provider, s.SourceId) })
               .ToDictionaryAsync(s => s.DeckId, s => s.Work);
}
