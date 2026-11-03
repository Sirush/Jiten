using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Jiten.Core.Data.WebNovel;
using static Jiten.Core.WebNovel.WebNovelParsing;

namespace Jiten.Core.WebNovel;

public sealed record KakuyomuWorkPage(WebNovelInfo Info, List<WebNovelEpisodeRef> Toc);

/// <summary>
/// Kakuyomu is a Next.js app: work and book pages embed their Apollo cache in __NEXT_DATA__, entities keyed
/// "Typename:id" and linked by {"__ref": "..."}. Episode pages are server-rendered HTML.
/// </summary>
public static partial class KakuyomuPageParser
{
    public static KakuyomuWorkPage ParseWorkPage(string html, string workId)
    {
        using var data = ReadNextData(html);
        var state = ApolloState(data.RootElement);

        if (!state.TryGetProperty($"Work:{workId}", out var work))
            throw new InvalidOperationException($"Kakuyomu work {workId} is missing from the page data (markup may have changed).");

        var info = new WebNovelInfo
        {
            Provider = WebNovelProvider.Kakuyomu,
            SourceId = workId,
            Url = WebNovelUrlParser.BuildWorkUrl(WebNovelProvider.Kakuyomu, workId),
            Title = GetString(work, "title") ?? string.Empty,
            Author = GetString(work, "alternateAuthorName") ?? AuthorName(state, work),
            Synopsis = Synopsis(work),
            Genre = GenreLabel(GetString(work, "genre")),
            Keywords = Keywords(work),
            FirstPublishedAt = GetDate(work, "publishedAt"),
            LastUpdatedAt = GetDate(work, "lastEpisodePublishedAt") ?? GetDate(work, "editedAt"),
            EpisodeCount = GetInt(work, "publicEpisodeCount"),
            TotalCharacters = GetInt(work, "totalCharacterCount"),
            IsCompleted = GetString(work, "serialStatus") == "COMPLETED",
            CoverUrl = GetString(work, "adminCoverImageUrl")
        };

        return new KakuyomuWorkPage(info, ParseToc(state, work, workId));
    }

    /// <summary>
    /// Ids of the works a book (publication) page links to as its web version
    /// </summary>
    public static List<string> ParseLinkedWorkIds(string html)
    {
        using var data = ReadNextData(html);

        return ApolloState(data.RootElement)
               .EnumerateObject()
               .Where(p => p.Name.StartsWith("Work:", StringComparison.Ordinal))
               .Select(p => p.Name["Work:".Length..])
               .Distinct()
               .ToList();
    }

    public static string ParseEpisodeText(string html, string url)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);

        var body = document.QuerySelector(".widget-episodeBody")
                   ?? throw new InvalidOperationException($"No novel text found at {url} (markup may have changed).");

        return ParagraphText(body, document);
    }

    private static List<WebNovelEpisodeRef> ParseToc(JsonElement state, JsonElement work, string workId)
    {
        if (!work.TryGetProperty("tableOfContentsV2", out var toc) || toc.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Kakuyomu work {workId} has no table of contents in the page data (markup may have changed).");

        var episodes = new List<WebNovelEpisodeRef>();

        foreach (var section in toc.EnumerateArray().Select(r => Deref(state, r)).OfType<JsonElement>())
        {
            var sectionTitle = section.TryGetProperty("chapter", out var chapterRef) && Deref(state, chapterRef) is { } chapter
                ? GetString(chapter, "title")
                : null;

            if (!section.TryGetProperty("episodeUnions", out var unions) || unions.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var union in unions.EnumerateArray())
            {
                if (RefKey(union) is not { } key || !key.StartsWith("Episode:", StringComparison.Ordinal) ||
                    Deref(state, union) is not { } episode)
                    continue;

                episodes.Add(new WebNovelEpisodeRef
                {
                    Number = episodes.Count + 1,
                    SourceEpisodeId = GetString(episode, "id") ?? key["Episode:".Length..],
                    Title = GetString(episode, "title") ?? string.Empty,
                    UpdatedAt = GetDate(episode, "publishedAt"),
                    SectionTitle = sectionTitle
                });
            }
        }

        return episodes;
    }

    private static string? AuthorName(JsonElement state, JsonElement work) =>
        work.TryGetProperty("author", out var authorRef) && Deref(state, authorRef) is { } author
            ? GetString(author, "activityName") ?? GetString(author, "name")
            : null;

    /// <summary>
    /// The キャッチコピー leads the page above the introduction, so it leads the description too
    /// </summary>
    private static string? Synopsis(JsonElement work)
    {
        var parts = new[] { GetString(work, "catchphrase"), GetString(work, "introduction") }
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p!.Trim())
                    .ToList();

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>
    /// Tags plus the self-declared content ratings, which Kakuyomu shows beside the tags the way Narou's
    /// R15 and 残酷な描写あり keywords sit among its keywords
    /// </summary>
    private static List<string> Keywords(JsonElement work)
    {
        var keywords = new List<string>();

        if (work.TryGetProperty("tagLabels", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            keywords.AddRange(tags.EnumerateArray()
                                  .Where(t => t.ValueKind == JsonValueKind.String)
                                  .Select(t => t.GetString()!.Trim())
                                  .Where(t => t.Length > 0));
        }

        if (GetBool(work, "isCruel"))
            keywords.Add("残酷描写有り");
        if (GetBool(work, "isViolent"))
            keywords.Add("暴力描写有り");
        if (GetBool(work, "isSexual"))
            keywords.Add("性描写有り");

        return keywords.Distinct().ToList();
    }

    /// <summary>
    /// Labels as kakuyomu.jp shows them; an unknown code passes through so it can still be mapped by hand
    /// </summary>
    private static string? GenreLabel(string? code) => code switch
    {
        null or "" => null,
        "FANTASY" => "異世界ファンタジー",
        "ACTION" => "現代ファンタジー",
        "SF" => "SF",
        "LOVE_STORY" => "恋愛",
        "ROMANCE" => "ラブコメ",
        "DRAMA" => "現代ドラマ",
        "HORROR" => "ホラー",
        "MYSTERY" => "ミステリー",
        "NONFICTION" => "エッセイ・ノンフィクション",
        "HISTORY" => "歴史・時代・伝奇",
        "CRITICISM" => "創作論・評論",
        "OTHERS" => "詩・童話・その他",
        "FAN_FICTION" => "二次創作",
        _ => code
    };

    private static JsonDocument ReadNextData(string html)
    {
        var match = NextDataScript().Match(html);
        if (!match.Success)
            throw new InvalidOperationException("Kakuyomu page has no __NEXT_DATA__ (markup may have changed).");

        var json = match.Groups[1];
        return JsonDocument.Parse(html.AsMemory(json.Index, json.Length));
    }

    private static JsonElement ApolloState(JsonElement root)
    {
        if (root.TryGetProperty("props", out var props) &&
            props.TryGetProperty("pageProps", out var pageProps) &&
            pageProps.TryGetProperty("__APOLLO_STATE__", out var state) &&
            state.ValueKind == JsonValueKind.Object)
            return state;

        throw new InvalidOperationException("Kakuyomu page data has no Apollo state (markup may have changed).");
    }

    private static string? RefKey(JsonElement reference) =>
        reference.ValueKind == JsonValueKind.Object && reference.TryGetProperty("__ref", out var key) &&
        key.ValueKind == JsonValueKind.String
            ? key.GetString()
            : null;

    private static JsonElement? Deref(JsonElement state, JsonElement reference) =>
        RefKey(reference) is { } key && state.TryGetProperty(key, out var entity) ? entity : null;

    private static bool GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>
    /// ISO 8601 with an explicit Z; returned as UTC because Npgsql only writes zero-offset DateTimeOffsets
    /// </summary>
    private static DateTimeOffset? GetDate(JsonElement e, string name) =>
        GetString(e, name) is { } value &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    [GeneratedRegex(@"<script id=""__NEXT_DATA__""[^>]*>(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex NextDataScript();
}
