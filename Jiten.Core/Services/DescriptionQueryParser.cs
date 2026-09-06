using System.Text.RegularExpressions;
using Jiten.Core.Data;

namespace Jiten.Core.Services;

/// <summary>
/// Splits a free-text media query into the media type it names ("a visual novel about ninja",
/// "アニメで探偵もの") and the text left to embed. Type words are stripped so they do not steer the
/// ranking toward descriptions that merely mention the medium.
/// </summary>
public static partial class DescriptionQueryParser
{
    public sealed record Parsed(string Text, MediaType? MediaType);

    // Specific media first, generic words ("novel", "drama") last, so "visual novel" and "audio drama" are never mis-cut.
    // Bare "game", "book" and "rpg" are left out: they appear inside plot descriptions far too often.
    private static readonly (string Phrase, MediaType Type)[] TypePhrases =
    [
        ("visual novels", MediaType.VisualNovel), ("visual novel", MediaType.VisualNovel), ("visualnovels", MediaType.VisualNovel), ("visualnovel", MediaType.VisualNovel),
        ("vns", MediaType.VisualNovel), ("vn", MediaType.VisualNovel), ("eroges", MediaType.VisualNovel), ("eroge", MediaType.VisualNovel), ("galge", MediaType.VisualNovel), ("otome game", MediaType.VisualNovel), ("otome games", MediaType.VisualNovel), ("otoge", MediaType.VisualNovel), ("nukige", MediaType.VisualNovel), ("kinetic novel", MediaType.VisualNovel),
        ("ビジュアルノベル", MediaType.VisualNovel), ("ノベルゲーム", MediaType.VisualNovel), ("ノベルゲー", MediaType.VisualNovel), ("エロゲー", MediaType.VisualNovel), ("エロゲ", MediaType.VisualNovel), ("ギャルゲー", MediaType.VisualNovel), ("ギャルゲ", MediaType.VisualNovel), ("乙女ゲーム", MediaType.VisualNovel), ("乙女ゲー", MediaType.VisualNovel), ("紙芝居ゲー", MediaType.VisualNovel), ("美少女ゲーム", MediaType.VisualNovel), ("美少女ゲー", MediaType.VisualNovel), ("サウンドノベル", MediaType.VisualNovel),
        ("web novels", MediaType.WebNovel), ("web novel", MediaType.WebNovel), ("webnovels", MediaType.WebNovel), ("webnovel", MediaType.WebNovel), ("wns", MediaType.WebNovel), ("wn", MediaType.WebNovel), ("narou", MediaType.WebNovel), ("syosetu", MediaType.WebNovel), ("kakuyomu", MediaType.WebNovel),
        ("なろう系", MediaType.WebNovel), ("なろう小説", MediaType.WebNovel), ("なろう", MediaType.WebNovel), ("web小説", MediaType.WebNovel), ("ウェブ小説", MediaType.WebNovel), ("ネット小説", MediaType.WebNovel), ("カクヨム", MediaType.WebNovel),
        ("light novels", MediaType.Novel), ("light novel", MediaType.Novel), ("lightnovels", MediaType.Novel), ("lightnovel", MediaType.Novel), ("lns", MediaType.Novel), ("ln", MediaType.Novel), ("ライトノベル", MediaType.Novel), ("ラノベ", MediaType.Novel),
        ("non-fiction", MediaType.NonFiction), ("nonfiction", MediaType.NonFiction), ("non fiction", MediaType.NonFiction), ("memoir", MediaType.NonFiction), ("biography", MediaType.NonFiction), ("ノンフィクション", MediaType.NonFiction), ("実用書", MediaType.NonFiction), ("新書", MediaType.NonFiction), ("自伝", MediaType.NonFiction),
        ("video games", MediaType.VideoGame), ("video game", MediaType.VideoGame), ("videogames", MediaType.VideoGame), ("videogame", MediaType.VideoGame), ("jrpgs", MediaType.VideoGame), ("jrpg", MediaType.VideoGame), ("rpgs", MediaType.VideoGame), ("ゲーム", MediaType.VideoGame), ("テレビゲーム", MediaType.VideoGame),
        ("audiobooks", MediaType.Audio), ("audiobook", MediaType.Audio), ("audio books", MediaType.Audio), ("audio book", MediaType.Audio), ("audio dramas", MediaType.Audio), ("audio drama", MediaType.Audio), ("drama cd", MediaType.Audio), ("drama cds", MediaType.Audio), ("podcasts", MediaType.Audio), ("podcast", MediaType.Audio), ("radio", MediaType.Audio),
        ("オーディオブック", MediaType.Audio), ("オーディオドラマ", MediaType.Audio), ("ドラマCD", MediaType.Audio), ("ドラマcd", MediaType.Audio), ("ラジオドラマ", MediaType.Audio), ("ラジオ", MediaType.Audio), ("ポッドキャスト", MediaType.Audio), ("朗読", MediaType.Audio),
        ("youtube", MediaType.YouTube), ("youtubers", MediaType.YouTube), ("youtuber", MediaType.YouTube),
        ("ユーチューブ", MediaType.YouTube), ("ユーチューバー", MediaType.YouTube),
        ("animes", MediaType.Anime), ("anime", MediaType.Anime), ("animated series", MediaType.Anime), ("cartoon", MediaType.Anime), ("アニメ", MediaType.Anime),
        ("j-dramas", MediaType.Drama), ("j-drama", MediaType.Drama), ("jdramas", MediaType.Drama), ("jdrama", MediaType.Drama), ("dorama", MediaType.Drama), ("dramas", MediaType.Drama), ("drama", MediaType.Drama), ("tv series", MediaType.Drama), ("tv show", MediaType.Drama), ("tv shows", MediaType.Drama), ("live action series", MediaType.Drama), ("live-action series", MediaType.Drama), ("ドラマ", MediaType.Drama), ("実写ドラマ", MediaType.Drama), ("テレビドラマ", MediaType.Drama), ("連続ドラマ", MediaType.Drama), ("大河", MediaType.Drama),
        ("movies", MediaType.Movie), ("movie", MediaType.Movie), ("films", MediaType.Movie), ("film", MediaType.Movie), ("live action movie", MediaType.Movie), ("live-action movie", MediaType.Movie), ("live action film", MediaType.Movie), ("live-action film", MediaType.Movie), ("映画", MediaType.Movie), ("実写映画", MediaType.Movie), ("劇場版", MediaType.Movie),
        ("manga", MediaType.Manga), ("mangas", MediaType.Manga), ("comic", MediaType.Manga), ("comics", MediaType.Manga), ("マンガ", MediaType.Manga), ("漫画", MediaType.Manga), ("コミック", MediaType.Manga),
        ("novels", MediaType.Novel), ("novel", MediaType.Novel), ("小説", MediaType.Novel), ("文庫", MediaType.Novel), ("単行本", MediaType.Novel),
    ];

    public static Parsed Parse(string query)
    {
        var text = query.Trim();
        MediaType? detected = null;
        foreach (var (phrase, type) in TypePhrases)
        {
            var match = FindPhrase(text, phrase);
            if (match == null)
                continue;
            detected = type;
            var removedAtEnd = match.Value.Index + match.Value.Length >= text.Length;
            text = Cleanup(text.Remove(match.Value.Index, match.Value.Length), removedAtEnd);
            break;
        }

        // A query that was only a type word ("anime") has nothing left to rank on; keep the original so results stay meaningful.
        return string.IsNullOrWhiteSpace(text) ? new Parsed(query.Trim(), detected) : new Parsed(text, detected);
    }

    private static (int Index, int Length)? FindPhrase(string text, string phrase)
    {
        var isAscii = phrase.All(c => c < 128);
        if (!isAscii)
        {
            var idx = text.IndexOf(phrase, StringComparison.Ordinal);
            return idx < 0 ? null : (idx, phrase.Length);
        }

        var m = Regex.Match(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(phrase)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase);
        return m.Success ? (m.Index, m.Length) : null;
    }

    /// <summary>Removes the article, preposition or particle that hung off the stripped type word ("a visual novel about ninja" -> "ninja", "恋愛の小説" -> "恋愛").</summary>
    private static string Cleanup(string text, bool removedAtEnd)
    {
        text = LeadingArticle().Replace(text, "");
        text = LeadingPreposition().Replace(text, "");
        text = LeadingJapaneseParticle().Replace(text, "");
        if (removedAtEnd)
            text = TrailingJapaneseParticle().Replace(text, "");
        text = MultiSpace().Replace(text, " ");
        return text.Trim(' ', ',', '、', '。', '.', '-');
    }

    [GeneratedRegex(@"^\s*(?:an?|the|some|good|great|any)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingArticle();

    [GeneratedRegex(@"^\s*(?:about|on|of|featuring|involving|regarding|concerning|where|that|which|in which)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingPreposition();

    [GeneratedRegex(@"^\s*(?:で|の|な|系の|系)\s*")]
    private static partial Regex LeadingJapaneseParticle();

    [GeneratedRegex(@"\s*(?:の|な|系の|系|っぽい|風の|風)\s*$")]
    private static partial Regex TrailingJapaneseParticle();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpace();
}
