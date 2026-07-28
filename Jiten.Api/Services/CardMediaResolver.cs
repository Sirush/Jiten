using Jiten.Core.Data.User;

namespace Jiten.Api.Services;

/// <summary>
/// Display-time fallback across sibling forms of a word. Exact-form media always wins. When a kind is
/// missing on the requested form, images inherit from the most recent sibling image; audio inherits only
/// when pronunciation is provably shared (the word has exactly one kana reading), so 明日's あした/あす/
/// みょうにち never share audio while 会う/あう may.
/// </summary>
public static class CardMediaResolver
{
    public record Resolved(UserCardMedia Media, bool Inherited, byte SourceReadingIndex);

    /// <param name="wordMedia">All of the user's media rows for this WordId (any form, any kind).</param>
    /// <param name="kanaFormCount">Number of kana (FormType == KanaForm) readings the word entry has.</param>
    public static (Resolved? Image, Resolved? Audio) Resolve(
        byte readingIndex, IReadOnlyList<UserCardMedia> wordMedia, int kanaFormCount)
    {
        return (
            ResolveKind(readingIndex, wordMedia, CardMediaKind.Image, allowInherit: true),
            ResolveKind(readingIndex, wordMedia, CardMediaKind.Audio, allowInherit: kanaFormCount == 1));
    }

    private static Resolved? ResolveKind(
        byte readingIndex, IReadOnlyList<UserCardMedia> wordMedia, CardMediaKind kind, bool allowInherit)
    {
        var exact = wordMedia.FirstOrDefault(m => m.Kind == kind && m.ReadingIndex == readingIndex);
        if (exact != null)
            return new Resolved(exact, false, readingIndex);

        if (!allowInherit)
            return null;

        var sibling = wordMedia
                      .Where(m => m.Kind == kind && m.ReadingIndex != readingIndex)
                      .OrderByDescending(m => m.CreatedAt)
                      .FirstOrDefault();

        return sibling == null ? null : new Resolved(sibling, true, sibling.ReadingIndex);
    }
}
