using Jiten.Core;
using Jiten.Core.Data.CommunityLists;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.Forum;

public static class ForumDiscussionSubjects
{
    /// <summary>
    /// The page a subject's forum topic embeds, or null when it gets no topic: drafts and subdecks would spawn threads nobody can reach.
    /// The forum keys topics on these URLs and its embedding host rows route them to a category by path, so changing a format orphans
    /// every existing topic of that kind.
    /// </summary>
    public static async Task<string?> EmbedUrlAsync(JitenDbContext context, string subject, int id, CancellationToken ct) =>
        subject switch
        {
            "list" => await context.CommunityLists.AnyAsync(l => l.ListId == id && l.Status == CommunityListStatus.Public, ct)
                ? $"https://jiten.moe/lists/{id}"
                : null,
            "deck" => await context.Decks.AnyAsync(d => d.DeckId == id && d.ParentDeckId == null, ct)
                ? $"https://jiten.moe/decks/media/{id}/detail"
                : null,
            _ => null
        };
}
