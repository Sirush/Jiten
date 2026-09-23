using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Utils;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public record DeckSearchHit(int DeckId, int TotalCount);

/// <summary>Title matching shared by the media search box and the request duplicate checks.</summary>
public class MediaTitleSearchService(JitenDbContext context)
{
    /// <summary>Extra edit distance the duplicate checks allow over the search box: a wrong hint costs nothing, a missed twin does.</summary>
    public const int DuplicateLevenshteinSlack = 1;

    private const int NoMediaTypeFilter = -1;

    private bool IsNpgsql => context.Database.ProviderName?.Contains("Npgsql") == true;

    public static int GetLevenshteinMaxDistance(string query, int slack = 0)
    {
        var baseDistance = query.Length switch
        {
            <= 5 => 1,
            <= 12 => 2,
            _ => 3
        };

        // Slack never lets more than half the query change, or a two-letter title would match every short title.
        return Math.Min(baseDistance + slack, Math.Max(baseDistance, (query.Length - 1) / 2));
    }

    /// <summary>Primary decks ranked exact, then PGroonga on titles, then on space-stripped titles; edit distance only when those find nothing.</summary>
    public async Task<List<DeckSearchHit>> SearchPrimaryDecks(string query, int limit, int levenshteinSlack = 0, MediaType? mediaType = null)
    {
        var filter = new TitleFilter(query);
        var mediaTypeValue = mediaType.HasValue ? (int)mediaType.Value : NoMediaTypeFilter;

        if (!IsNpgsql)
            return await SubstringDeckMatches(filter.Original, limit, mediaType);

        var results = await RankedDeckMatches(filter, limit, mediaTypeValue);
        if (results.Count == 0)
            results = await LevenshteinDeckMatches(filter, limit, GetLevenshteinMaxDistance(filter.Original, levenshteinSlack), mediaTypeValue);

        return results;
    }

    /// <summary>Open and in-progress request ids, exact titles first, then by edit distance and votes.</summary>
    /// <param name="fulfillableOnly">Keeps only New requests not yet linked to a deck, the ones adding a media can fulfil.</param>
    public async Task<List<int>> SearchActiveRequests(string query, int limit, int levenshteinSlack, MediaType? mediaType = null,
        bool fulfillableOnly = false)
    {
        var filter = new TitleFilter(query);

        if (!IsNpgsql)
            return await SubstringRequestMatches(filter.Original, limit, mediaType, fulfillableOnly);

        var mediaTypeValue = mediaType.HasValue ? (int)mediaType.Value : NoMediaTypeFilter;
        var maxDist = GetLevenshteinMaxDistance(filter.Original, levenshteinSlack);
        var open = (int)MediaRequestStatus.Open;
        var inProgress = (int)MediaRequestStatus.InProgress;
        var newKind = (int)MediaRequestKind.New;

        // Plain &@ rather than &@~: request titles are free text, and query syntax would reject stray parentheses or colons.
        FormattableString sql = $$"""
                                  WITH candidates AS (
                                      SELECT r."Id"
                                      FROM jiten."MediaRequests" r
                                      WHERE r."Status" IN ({{open}}, {{inProgress}})
                                        AND (r."Title" &@ {{filter.Original}} OR ({{filter.HasRomajiVariant}} AND r."Title" &@ {{filter.Romaji}}))
                                      UNION
                                      SELECT r."Id"
                                      FROM jiten."MediaRequests" r
                                      WHERE r."Status" IN ({{open}}, {{inProgress}})
                                        AND (levenshtein(LEFT(LOWER(r."Title"), 255), LEFT(LOWER({{filter.Original}}), 255)) <= {{maxDist}}
                                          OR levenshtein(LEFT(LOWER(REPLACE(r."Title", ' ', '')), 255), LEFT(LOWER({{filter.NoSpaces}}), 255)) <= {{maxDist}})
                                  )
                                  SELECT r."Id" AS "Value"
                                  FROM candidates c
                                  JOIN jiten."MediaRequests" r ON r."Id" = c."Id"
                                  WHERE ({{mediaTypeValue}} < 0 OR r."MediaType" = {{mediaTypeValue}})
                                    AND (NOT {{fulfillableOnly}} OR (r."Kind" = {{newKind}} AND r."FulfilledDeckId" IS NULL))
                                  ORDER BY CASE WHEN LOWER(TRIM(r."Title")) = LOWER({{filter.Original}})
                                                  OR LOWER(REPLACE(r."Title", ' ', '')) = LOWER({{filter.NoSpaces}})
                                                THEN 0 ELSE 1 END,
                                           LEAST(
                                               levenshtein(LEFT(LOWER(r."Title"), 255), LEFT(LOWER({{filter.Original}}), 255)),
                                               levenshtein(LEFT(LOWER(REPLACE(r."Title", ' ', '')), 255), LEFT(LOWER({{filter.NoSpaces}}), 255))
                                           ),
                                           r."UpvoteCount" DESC
                                  LIMIT {{limit}}
                                  """;

        return await context.Database.SqlQuery<int>(sql).ToListAsync();
    }

    private async Task<List<DeckSearchHit>> RankedDeckMatches(TitleFilter filter, int limit, int mediaTypeValue)
    {
        var queryLength = filter.Original.Length;

        FormattableString sql = $$"""
                                  WITH exact_matches AS (
                                      SELECT DISTINCT dt."DeckId",
                                             0 AS match_priority,
                                             100.0 AS score,
                                             dt."TitleType",
                                             LENGTH(dt."Title") AS title_length
                                      FROM jiten."DeckTitles" dt
                                      WHERE LOWER(dt."Title") = LOWER({{filter.Original}})
                                         OR LOWER(dt."TitleNoSpaces") = LOWER({{filter.NoSpaces}})
                                         OR ({{filter.HasRomajiVariant}} AND (LOWER(dt."Title") = {{filter.Romaji}} OR LOWER(dt."TitleNoSpaces") = {{filter.RomajiNoSpaces}}))
                                  ),
                                  fuzzy_title_matches AS (
                                      SELECT dt."DeckId",
                                             1 AS match_priority,
                                             pgroonga_score(dt.tableoid, dt.ctid) AS score,
                                             dt."TitleType",
                                             LENGTH(dt."Title") AS title_length
                                      FROM jiten."DeckTitles" dt
                                      WHERE (dt."Title" &@~ {{filter.Original}} OR ({{filter.HasRomajiVariant}} AND dt."Title" &@~ {{filter.Romaji}}))
                                        AND dt."DeckId" NOT IN (SELECT "DeckId" FROM exact_matches)
                                  ),
                                  fuzzy_nospace_matches AS (
                                      SELECT dt."DeckId",
                                             2 AS match_priority,
                                             pgroonga_score(dt.tableoid, dt.ctid) AS score,
                                             dt."TitleType",
                                             LENGTH(dt."TitleNoSpaces") AS title_length
                                      FROM jiten."DeckTitles" dt
                                      WHERE dt."TitleType" IN (1, 3)
                                        AND (dt."TitleNoSpaces" &@~ {{filter.NoSpaces}} OR ({{filter.HasRomajiVariant}} AND dt."TitleNoSpaces" &@~ {{filter.RomajiNoSpaces}}))
                                        AND dt."DeckId" NOT IN (SELECT "DeckId" FROM exact_matches)
                                        AND dt."DeckId" NOT IN (SELECT "DeckId" FROM fuzzy_title_matches)
                                  ),
                                  all_matches AS (
                                      SELECT * FROM exact_matches
                                      UNION ALL
                                      SELECT * FROM fuzzy_title_matches
                                      UNION ALL
                                      SELECT * FROM fuzzy_nospace_matches
                                  ),
                                  ranked AS (
                                      SELECT "DeckId",
                                             MIN(match_priority) AS best_match,
                                             MIN(CASE "TitleType"
                                                 WHEN 0 THEN 1
                                                 WHEN 1 THEN 2
                                                 WHEN 2 THEN 3
                                                 ELSE 4
                                             END) AS best_type,
                                             MAX(score) AS best_score,
                                             {{queryLength}}::float / NULLIF(MIN(title_length), 0)::float AS length_ratio
                                      FROM all_matches
                                      GROUP BY "DeckId"
                                  )
                                  SELECT r."DeckId", COUNT(*) OVER() AS "TotalCount"
                                  FROM ranked r
                                  JOIN jiten."Decks" d ON r."DeckId" = d."DeckId"
                                  WHERE d."ParentDeckId" IS NULL
                                    AND ({{mediaTypeValue}} < 0 OR d."MediaType" = {{mediaTypeValue}})
                                  ORDER BY r.best_match ASC, r.length_ratio DESC, r.best_type ASC, r.best_score DESC
                                  LIMIT {{limit}}
                                  """;

        return await context.Database.SqlQuery<DeckSearchHit>(sql).ToListAsync();
    }

    private async Task<List<DeckSearchHit>> LevenshteinDeckMatches(TitleFilter filter, int limit, int maxDist, int mediaTypeValue)
    {
        FormattableString sql = $$"""
                                  SELECT m."DeckId", COUNT(*) OVER() AS "TotalCount"
                                  FROM (
                                      SELECT DISTINCT ON (dt."DeckId") dt."DeckId",
                                             LEAST(
                                                 levenshtein(LEFT(LOWER(dt."Title"), 255), LEFT(LOWER({{filter.Original}}), 255)),
                                                 levenshtein(LEFT(LOWER(dt."TitleNoSpaces"), 255), LEFT(LOWER({{filter.NoSpaces}}), 255))
                                             ) AS distance,
                                             LENGTH(dt."Title") AS title_length
                                      FROM jiten."DeckTitles" dt
                                      JOIN jiten."Decks" d ON dt."DeckId" = d."DeckId"
                                      WHERE d."ParentDeckId" IS NULL
                                        AND ({{mediaTypeValue}} < 0 OR d."MediaType" = {{mediaTypeValue}})
                                        AND (levenshtein(LEFT(LOWER(dt."Title"), 255), LEFT(LOWER({{filter.Original}}), 255)) <= {{maxDist}}
                                          OR levenshtein(LEFT(LOWER(dt."TitleNoSpaces"), 255), LEFT(LOWER({{filter.NoSpaces}}), 255)) <= {{maxDist}})
                                      ORDER BY dt."DeckId", distance ASC, title_length ASC
                                  ) m
                                  ORDER BY m.distance ASC, m.title_length ASC
                                  LIMIT {{limit}}
                                  """;

        return await context.Database.SqlQuery<DeckSearchHit>(sql).ToListAsync();
    }

    private async Task<List<DeckSearchHit>> SubstringDeckMatches(string filter, int limit, MediaType? mediaType)
    {
        var lowered = filter.ToLowerInvariant();
        var query = context.DeckTitles.AsNoTracking()
                           .Where(dt => dt.Deck!.ParentDeckId == null && dt.Title.ToLower().Contains(lowered));

        if (mediaType.HasValue)
            query = query.Where(dt => dt.Deck!.MediaType == mediaType.Value);

        var hits = await query.GroupBy(dt => dt.DeckId)
                              .Select(g => new { DeckId = g.Key, Length = g.Min(dt => dt.Title.Length) })
                              .OrderBy(h => h.Length)
                              .Take(limit)
                              .ToListAsync();

        return hits.Select(h => new DeckSearchHit(h.DeckId, hits.Count)).ToList();
    }

    private async Task<List<int>> SubstringRequestMatches(string filter, int limit, MediaType? mediaType, bool fulfillableOnly)
    {
        var lowered = filter.ToLowerInvariant();
        var query = context.MediaRequests.AsNoTracking()
                           .Where(r => r.Title.ToLower().Contains(lowered))
                           .WhereActive(mediaType, fulfillableOnly);

        return await query.OrderBy(r => r.Title.Length)
                          .ThenByDescending(r => r.UpvoteCount)
                          .Take(limit)
                          .Select(r => r.Id)
                          .ToListAsync();
    }

    private sealed class TitleFilter
    {
        public TitleFilter(string query)
        {
            Original = query.Trim();
            Romaji = TextNormalizationHelper.ContainsRomaji(Original)
                ? TextNormalizationHelper.NormaliseRomaji(Original)
                : Original;
            HasRomajiVariant = Romaji != Original.ToLowerInvariant();
            NoSpaces = Original.Replace(" ", "");
            RomajiNoSpaces = Romaji.Replace(" ", "");
        }

        public string Original { get; }
        public string Romaji { get; }
        public bool HasRomajiVariant { get; }
        public string NoSpaces { get; }
        public string RomajiNoSpaces { get; }
    }
}
