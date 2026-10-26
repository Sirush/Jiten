using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.CommunityLists;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.Forum;

/// <summary>The topic created when a reader starts the discussion of a subject; <see cref="Subject"/> picks its forum category.</summary>
public record ForumTopicDraft(ForumSubject Subject, string EmbedUrl, string Title, string Raw);

public static class ForumDiscussionSubjects
{
    public static ForumSubject? Parse(string subject) =>
        subject switch
        {
            "list" => ForumSubject.List,
            "deck" => ForumSubject.Deck,
            _ => null
        };

    /// <summary>
    /// The page a subject's forum topic embeds, or null when it gets no topic: drafts and subdecks would spawn threads nobody can reach.
    /// The forum keys topics on these URLs, so changing a format orphans every existing topic of that kind.
    /// </summary>
    public static async Task<string?> EmbedUrlAsync(JitenDbContext context, ForumSubject subject, int id, CancellationToken ct) =>
        subject switch
        {
            ForumSubject.List => await PublicLists(context, id).AnyAsync(ct) ? ListUrl(id) : null,
            ForumSubject.Deck => await ParentDecks(context, id).AnyAsync(ct) ? DeckUrl(id) : null,
            _ => null
        };

    public static async Task<ForumTopicDraft?> DraftAsync(JitenDbContext context, ForumSubject subject, int id, CancellationToken ct)
    {
        switch (subject)
        {
            case ForumSubject.List:
                var list = await PublicLists(context, id).Select(l => new { l.Name, l.Description }).FirstOrDefaultAsync(ct);
                if (list == null) return null;
                var listUrl = ListUrl(id);
                return new ForumTopicDraft(subject, listUrl, list.Name.Trim(), Body(listUrl, list.Description));
            case ForumSubject.Deck:
                var deck = await ParentDecks(context, id).Select(d => new { d.OriginalTitle, d.RomajiTitle, d.Description }).FirstOrDefaultAsync(ct);
                if (deck == null) return null;
                var deckUrl = DeckUrl(id);
                return new ForumTopicDraft(subject, deckUrl, DeckTitle(deck.OriginalTitle, deck.RomajiTitle), Body(deckUrl, deck.Description));
            default:
                return null;
        }
    }

    /// <summary>Fixed format so forum titles never depend on the title language of whoever started the discussion; the original follows only when it differs.</summary>
    public static string DeckTitle(string originalTitle, string? romajiTitle)
    {
        var original = originalTitle.Trim();
        var romaji = romajiTitle?.Trim();
        return string.IsNullOrEmpty(romaji) || romaji.Equals(original, StringComparison.OrdinalIgnoreCase) ? original : $"{romaji} ({original})";
    }

    /// <summary>The page URL sits alone on the first line so the forum oneboxes it into a card with the page's cover and summary.</summary>
    public static string Body(string embedUrl, string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return embedUrl;
        var quoted = description.Trim().ReplaceLineEndings("\n").Split('\n').Select(line => "> " + line);
        return $"{embedUrl}\n\n{string.Join('\n', quoted)}";
    }

    private static IQueryable<CommunityList> PublicLists(JitenDbContext context, int id) =>
        context.CommunityLists.AsNoTracking().Where(l => l.ListId == id && l.Status == CommunityListStatus.Public);

    private static IQueryable<Deck> ParentDecks(JitenDbContext context, int id) =>
        context.Decks.AsNoTracking().Where(d => d.DeckId == id && d.ParentDeckId == null);

    private static string ListUrl(int id) => $"https://jiten.moe/lists/{id}";

    private static string DeckUrl(int id) => $"https://jiten.moe/decks/media/{id}/detail";
}
