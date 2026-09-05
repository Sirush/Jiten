using System.Linq.Expressions;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using AnkiNet;
using Hangfire;
using Jiten.Api.Authorization;
using Jiten.Api.Dtos;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Enums;
using Jiten.Api.Helpers;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WanaKanaShaapu;
using Jiten.Core.Utils;
using Microsoft.AspNetCore.Authorization;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

/// <summary>
/// Endpoints for browsing media decks, vocabulary, downloads and related statistics.
/// </summary>
[ApiController]
[Route("api/media-deck")]
[EnableRateLimiting("fixed")]
[Produces("application/json")]
[SwaggerTag("Media decks and vocabulary")]
public class MediaDeckController(
    JitenDbContext context,
    IDbContextFactory<JitenDbContext> contextFactory,
    UserDbContext userContext,
    ICurrentUserService currentUserService,
    IConfiguration configuration,
    ILogger<MediaDeckController> logger,
    IHttpClientFactory httpClientFactory,
    IDeckWordResolver deckWordResolver,
    IFrequencySourceResolver frequencySourceResolver,
    IDeckDownloadService downloadService,
    IBackgroundJobClient backgroundJobClient,
    ICoverageJourneyService coverageJourneyService,
    Jiten.Core.Services.DeckVectorService deckVectorService,
    DescriptionSearchService descriptionSearchService,
    IDeckActivityBuffer activityBuffer) : ControllerBase
{
    private record DeckIdWithCount(int DeckId, int TotalCount);

    private class DeckWithOccurrences
    {
        public Deck Deck { get; set; } = null!;
        public int Occurrences { get; set; }
    }

    /// <summary>
    /// Returns the IDs of all parent media decks.
    /// </summary>
    /// <returns>List of deck IDs.</returns>
    [HttpGet("get-media-decks-id")]
    [ResponseCache(Duration = 60 * 60)]
    [SwaggerOperation(Summary = "Get IDs of top-level media decks")]
    [ProducesResponseType(typeof(List<int>), StatusCodes.Status200OK)]
    public async Task<List<int>> GetMediaDecksId()
    {
        return await context.Decks.AsNoTracking().Where(d => d.ParentDeckId == null).Select(d => d.DeckId).ToListAsync();
    }

    /// <summary>
    /// Returns top-level media decks with the fields needed for sitemap entries (lastmod + cover image).
    /// </summary>
    [HttpGet("get-media-decks-sitemap")]
    [ResponseCache(Duration = 60 * 60)]
    [SwaggerOperation(Summary = "Get top-level media decks for sitemap generation")]
    [ProducesResponseType(typeof(List<MediaDeckSitemapEntry>), StatusCodes.Status200OK)]
    public async Task<List<MediaDeckSitemapEntry>> GetMediaDecksForSitemap()
    {
        return await context.Decks.AsNoTracking()
                            .Where(d => d.ParentDeckId == null)
                            .Select(d => new MediaDeckSitemapEntry(d.DeckId, d.LastUpdate, d.CoverName))
                            .ToListAsync();
    }

    /// <summary>
    /// Returns the deck dto of all parent media decks.
    /// </summary>
    /// <returns>List of decks with titles and ids.</returns>
    [HttpGet("get-media-decks-by-type/{mediaType}")]
    [ResponseCache(Duration = 60 * 60)]
    [SwaggerOperation(Summary = "Get list of top-level media decks by type")]
    [ProducesResponseType(typeof(List<DeckDto>), StatusCodes.Status200OK)]
    public async Task<List<DeckDto>> GetMediaDecksByType(MediaType mediaType)
    {
        var decks = await context.Decks.AsNoTracking().Where(d => d.ParentDeckId == null && d.MediaType == mediaType)
                                 .OrderBy(d => d.RomajiTitle)
                                 .Include(d => d.Links)
                                 .Include(d => d.Titles)
                                 .Include(d => d.DeckDifficulty)
                                 .AsSplitQuery()
                                 .ToListAsync();
        var dtos = new List<DeckDto>();
        foreach (var deck in decks)
        {
            dtos.Add(new DeckDto(deck));
        }

        return dtos;
    }

    /// <summary>
    /// Returns slim difficulty-ranked rows for the public media-type hub pages
    /// </summary>
    [HttpGet("get-media-decks-by-type-ranked/{mediaType}")]
    [ResponseCache(Duration = 60 * 60, VaryByQueryKeys = ["page", "descending"])]
    [SwaggerOperation(Summary = "Get difficulty-ranked media decks by type (slim rows)")]
    [ProducesResponseType(typeof(PaginatedResponse<List<DeckRankingRowDto>>), StatusCodes.Status200OK)]
    public async Task<PaginatedResponse<List<DeckRankingRowDto>>> GetMediaDecksByTypeRanked(MediaType mediaType, int page = 1,
                                                                                            bool descending = false)
    {
        const int pageSize = 500;
        page = Math.Max(page, 1);

        var query = context.Decks.AsNoTracking()
                           .Where(d => d.ParentDeckId == null && d.MediaType == mediaType && d.Difficulty > -1);

        var totalItems = await query.CountAsync();

        var ordered = descending
            ? query.OrderByDescending(d => (d.DifficultyOverride > -1 ? d.DifficultyOverride : d.Difficulty)
                                          + (float)(d.DeckDifficulty != null ? d.DeckDifficulty.UserAdjustment : 0))
                   .ThenBy(d => d.DeckId)
            : query.OrderBy(d => (d.DifficultyOverride > -1 ? d.DifficultyOverride : d.Difficulty)
                                + (float)(d.DeckDifficulty != null ? d.DeckDifficulty.UserAdjustment : 0))
                   .ThenBy(d => d.DeckId);

        var rows = await ordered.Skip((page - 1) * pageSize)
                                .Take(pageSize)
                                .Select(d => new DeckRankingRowDto
                                {
                                    DeckId = d.DeckId,
                                    OriginalTitle = d.OriginalTitle,
                                    RomajiTitle = d.RomajiTitle ?? "",
                                    EnglishTitle = d.EnglishTitle ?? "",
                                    Difficulty = (d.DifficultyOverride > -1 ? d.DifficultyOverride : d.Difficulty)
                                                 + (float)(d.DeckDifficulty != null ? d.DeckDifficulty.UserAdjustment : 0),
                                    CharacterCount = d.CharacterCount,
                                    ReleaseYear = d.ReleaseDate.Year > 1 ? d.ReleaseDate.Year : null,
                                })
                                .ToListAsync();

        return new PaginatedResponse<List<DeckRankingRowDto>>(rows, totalItems, pageSize, (page - 1) * pageSize);
    }

    /// <summary>
    /// Returns decks most semantically similar to the given deck, based on precomputed FastText
    /// embedding cosine similarity over content words.
    /// </summary>
    /// <param name="deckId">The deck to find similar media for.</param>
    /// <param name="limit">Maximum number of results (default 10, max 100).</param>
    /// <param name="mediaType">Optional media type filter.</param>
    /// <returns>Ranked list of similar decks with similarity scores.</returns>
    [HttpGet("get-similar-decks/{deckId:int}")]
    [ResponseCache(Duration = 60 * 60, VaryByQueryKeys = ["limit", "mediaType"], VaryByHeader = "Authorization")]
    [SwaggerOperation(Summary = "Get semantically-similar media decks")]
    [ProducesResponseType(typeof(List<SimilarDeckDto>), StatusCodes.Status200OK)]
    public async Task<List<SimilarDeckDto>> GetSimilarDecks(int deckId, [FromQuery] int limit = 10, [FromQuery] MediaType? mediaType = null)
    {
        limit = Math.Clamp(limit, 1, 100);

        // Over-fetch when filtering by media type so the filter doesn't starve the result set.
        // The service picks the right similarity strategy (short-regime gating vs pure cosine) itself.
        var fetch = mediaType == null ? limit : Math.Min(limit * 8, 400);
        var sims = await deckVectorService.FindSimilarForAsync(deckId, fetch);
        return await HydrateRankedDecks(sims, limit, mediaType);
    }

    /// <summary>
    /// Natural-language media search: ranks decks by how well their description matches the
    /// query ("slow-burn romance in a rural town", "探偵もの"). A media type named in the query
    /// ("a visual novel about ninja") becomes the filter unless an explicit one is passed.
    /// </summary>
    /// <param name="query">Free-text description of what to find, English or Japanese.</param>
    /// <param name="limit">Maximum number of results (default 20, max 100).</param>
    /// <param name="mediaType">Optional media type filter; overrides a type named in the query.</param>
    [HttpGet("search-by-description")]
    [EnableRateLimiting("heavy")]
    [ResponseCache(Duration = 60 * 10, VaryByQueryKeys = ["query", "limit", "mediaType"], VaryByHeader = "Authorization")]
    [SwaggerOperation(Summary = "Search media by describing it")]
    [ProducesResponseType(typeof(DescriptionSearchResponseDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<DescriptionSearchResponseDto>> SearchByDescription([FromQuery] string query, [FromQuery] int limit = 20,
                                                                                      [FromQuery] MediaType? mediaType = null)
    {
        query = (query ?? "").Trim();
        if (query.Length is < 2 or > 500)
            return BadRequest("Query must be between 2 and 500 characters.");
        if (!descriptionSearchService.IsAvailable)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Description search is not available.");

        limit = Math.Clamp(limit, 1, 100);
        var parsed = DescriptionQueryParser.Parse(query);
        var effectiveType = mediaType ?? parsed.MediaType;
        HashSet<int>? allowed = null;
        if (effectiveType != null)
            allowed = await context.Decks.AsNoTracking()
                                   .Where(d => d.ParentDeckId == null && d.MediaType == effectiveType)
                                   .Select(d => d.DeckId)
                                   .ToHashSetAsync();
        var matches = descriptionSearchService.Search(parsed.Text, limit, allowed);
        var results = await HydrateRankedDecks(matches.Select(m => (m.DeckId, m.Score)).ToList(), limit, mediaType: null);
        return new DescriptionSearchResponseDto
        {
            Query = query,
            SearchedText = parsed.Text,
            DetectedMediaType = parsed.MediaType,
            MediaType = effectiveType,
            Results = results
        };
    }

    /// <summary>Applies the media-type filter to a ranked candidate list, then loads only the surviving decks.</summary>
    private async Task<List<SimilarDeckDto>> HydrateRankedDecks(List<(int DeckId, float Similarity)> sims, int limit, MediaType? mediaType)
    {
        if (sims.Count == 0)
            return new List<SimilarDeckDto>();

        var candidateIds = sims.Select(s => s.DeckId).ToList();

        // Cheap projection: which candidates pass the media-type filter, without hydrating any graphs.
        var matchingIds = await context.Decks.AsNoTracking()
                                       .Where(d => candidateIds.Contains(d.DeckId) && (mediaType == null || d.MediaType == mediaType))
                                       .Select(d => d.DeckId)
                                       .ToHashSetAsync();

        // Take the top `limit` survivors in similarity order, then hydrate only those decks.
        var finalIds = new List<int>(limit);
        foreach (var s in sims)
        {
            if (!matchingIds.Contains(s.DeckId))
                continue;
            finalIds.Add(s.DeckId);
            if (finalIds.Count >= limit)
                break;
        }

        if (finalIds.Count == 0)
            return new List<SimilarDeckDto>();

        var decks = await context.Decks.AsNoTracking()
                                 .Where(d => finalIds.Contains(d.DeckId))
                                 .Include(d => d.Links)
                                 .Include(d => d.Titles)
                                 .Include(d => d.DeckDifficulty)
                                 .AsSplitQuery()
                                 .ToDictionaryAsync(d => d.DeckId);

        var similarityById = sims.ToDictionary(s => s.DeckId, s => s.Similarity);
        var result = new List<SimilarDeckDto>(finalIds.Count);
        foreach (var id in finalIds)
        {
            if (!decks.TryGetValue(id, out var deck))
                continue;

            result.Add(new SimilarDeckDto { Deck = new DeckDto(deck), Similarity = similarityById[id] });
        }

        // Decorate with the viewer's coverage so the frontend can render coverage borders.
        // Per-user data must not be shared from the response cache.
        if (currentUserService.IsAuthenticated)
        {
            Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            var userId = currentUserService.UserId!;
            var coverages = await UserCoverageChunkHelper.GetCoverage(userContext, userId, result.Select(r => r.Deck.DeckId).ToList());
            coverages.ApplyTo(result.Select(r => r.Deck));
        }

        return result;
    }

    /// <summary>
    /// Returns the distinct media types present among a deck's most similar media, so the
    /// frontend type filter can offer every type that actually has results — not just those
    /// that happen to fall in the top unfiltered page.
    /// </summary>
    /// <param name="deckId">The deck to find similar media for.</param>
    /// <returns>Distinct media types available in the similarity candidate pool.</returns>
    [HttpGet("get-similar-deck-types/{deckId:int}")]
    [ResponseCache(Duration = 60 * 60)]
    [SwaggerOperation(Summary = "Get media types available among similar decks")]
    [ProducesResponseType(typeof(List<MediaType>), StatusCodes.Status200OK)]
    public async Task<List<MediaType>> GetSimilarDeckTypes(int deckId)
    {
        // Mirror the over-fetch cap in GetSimilarDecks: a type is selectable iff a filtered
        // query could actually surface it, i.e. it appears within the same candidate pool.
        const int candidatePool = 400;
        var sims = await deckVectorService.FindSimilarForAsync(deckId, candidatePool);
        if (sims.Count == 0)
            return new List<MediaType>();

        var candidateIds = sims.Select(s => s.DeckId).ToList();
        return await context.Decks.AsNoTracking()
                            .Where(d => candidateIds.Contains(d.DeckId))
                            .Select(d => d.MediaType)
                            .Distinct()
                            .ToListAsync();
    }

    /// <summary>
    /// Returns the full connected component of related media around a deck (sequels, adaptations,
    /// spin-offs, etc.), traversed across <see cref="DeckRelationship"/> edges ignoring direction.
    /// </summary>
    /// <param name="deckId">The deck whose franchise to expand.</param>
    /// <returns>Franchise nodes + edges; <c>truncated</c> is true if the node cap stopped expansion.</returns>
    [HttpGet("{deckId:int}/franchise")]
    [ResponseCache(Duration = 3600, VaryByHeader = "Authorization")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Get the franchise graph for a media deck")]
    [ProducesResponseType(typeof(FranchiseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FranchiseDto>> GetFranchise(int deckId)
    {
        const int nodeCap = 100;

        if (!await context.Decks.AsNoTracking().AnyAsync(d => d.DeckId == deckId))
            return NotFound();

        // BFS over relationship edges, treating them as undirected. Each frontier round pulls every
        // edge touching a frontier node in a single query; visited guards against revisiting nodes
        // (Alternative webs form legitimate cycles). The same physical edge can surface from either
        // endpoint, so edges are de-duplicated by (Source, Target, Type).
        var visited = new HashSet<int> { deckId };
        var frontier = new List<int> { deckId };
        var edges = new Dictionary<(int, int, DeckRelationshipType), FranchiseEdgeDto>();
        var truncated = false;

        while (frontier.Count > 0 && !truncated)
        {
            var batch = await context.DeckRelationships.AsNoTracking()
                                     .Where(r => frontier.Contains(r.SourceDeckId) || frontier.Contains(r.TargetDeckId))
                                     .ToListAsync();

            var nextFrontier = new List<int>();
            foreach (var r in batch)
            {
                edges.TryAdd((r.SourceDeckId, r.TargetDeckId, r.RelationshipType), new FranchiseEdgeDto
                {
                    SourceDeckId = r.SourceDeckId,
                    TargetDeckId = r.TargetDeckId,
                    RelationshipType = r.RelationshipType
                });

                foreach (var neighbour in new[] { r.SourceDeckId, r.TargetDeckId })
                {
                    if (visited.Contains(neighbour))
                        continue;

                    if (visited.Count >= nodeCap)
                    {
                        truncated = true;
                        break;
                    }

                    visited.Add(neighbour);
                    nextFrontier.Add(neighbour);
                }

                if (truncated)
                    break;
            }

            frontier = nextFrontier;
        }

        // Drop any edge that points at a node we never admitted (only possible once truncated).
        var nodeIds = visited;
        var keptEdges = edges.Values
                             .Where(e => nodeIds.Contains(e.SourceDeckId) && nodeIds.Contains(e.TargetDeckId))
                             .ToList();

        var nodeIdList = nodeIds.ToList();
        var childCounts = await context.Decks.AsNoTracking()
                                       .Where(d => d.ParentDeckId != null && nodeIdList.Contains(d.ParentDeckId.Value))
                                       .GroupBy(d => d.ParentDeckId!.Value)
                                       .Select(g => new { ParentId = g.Key, Count = g.Count() })
                                       .ToDictionaryAsync(x => x.ParentId, x => x.Count);

        var decks = await context.Decks.AsNoTracking()
                                 .Where(d => nodeIdList.Contains(d.DeckId))
                                 .Include(d => d.DeckDifficulty)
                                 .ToListAsync();

        var nodes = decks.Select(d => new FranchiseNodeDto
        {
            DeckId = d.DeckId,
            OriginalTitle = d.OriginalTitle,
            RomajiTitle = d.RomajiTitle ?? "",
            EnglishTitle = d.EnglishTitle ?? "",
            CoverName = d.CoverName,
            MediaType = d.MediaType,
            ReleaseDate = d.ReleaseDate.ToDateTime(new TimeOnly()),
            Difficulty = DifficultyMapper.MapDeck(d),
            DifficultyRaw = DifficultyMapper.GetAdjustedDifficulty(d),
            CharacterCount = d.CharacterCount,
            WordCount = d.WordCount,
            ChildrenDeckCount = childCounts.GetValueOrDefault(d.DeckId)
        }).ToList();

        // Decorate with the viewer's coverage so the frontend can render coverage borders.
        // Per-user data must not be shared from the response cache (mirrors GetSimilarDecks).
        if (currentUserService.IsAuthenticated)
        {
            Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            var userId = currentUserService.UserId!;
            var coverages = await UserCoverageChunkHelper.GetCoverage(userContext, userId, nodeIdList);
            foreach (var node in nodes)
            {
                if (coverages.MatureCoverage.TryGetValue(node.DeckId, out var c)) node.Coverage = c;
                if (coverages.MatureUniqueCoverage.TryGetValue(node.DeckId, out var uc)) node.UniqueCoverage = uc;
            }
        }

        return new FranchiseDto { Nodes = nodes, Edges = keptEdges, Truncated = truncated };
    }

    /// <summary>
    /// Returns lightweight media deck suggestions for autocomplete search.
    /// </summary>
    /// <param name="query">Search query (minimum 2 characters).</param>
    /// <param name="limit">Maximum number of results (default 5, max 10).</param>
    /// <returns>Media suggestions with total count.</returns>
    [HttpGet("search-suggestions")]
    [ResponseCache(Duration = 60, VaryByQueryKeys = ["query", "limit"])]
    [SwaggerOperation(Summary = "Get media suggestions for autocomplete")]
    [ProducesResponseType(typeof(MediaSuggestionsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MediaSuggestionsResponse>> GetSearchSuggestions(
        [FromQuery] string? query,
        [FromQuery] int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return Ok(new MediaSuggestionsResponse());

        limit = Math.Clamp(limit, 1, 10);

        var originalFilter = query.Trim();
        var romajiFilter = TextNormalizationHelper.ContainsRomaji(originalFilter)
            ? TextNormalizationHelper.NormaliseRomaji(originalFilter)
            : originalFilter;
        var hasRomajiVariant = romajiFilter != originalFilter.ToLowerInvariant();
        var filterNoSpaces = originalFilter.Replace(" ", "");
        var romajiFilterNoSpaces = romajiFilter.Replace(" ", "");
        var queryLength = originalFilter.Length;

        FormattableString sql = $$"""
                                  WITH exact_matches AS (
                                      SELECT DISTINCT dt."DeckId",
                                             0 AS match_priority,
                                             100.0 AS score,
                                             dt."TitleType",
                                             LENGTH(dt."Title") AS title_length
                                      FROM jiten."DeckTitles" dt
                                      WHERE LOWER(dt."Title") = LOWER({{originalFilter}})
                                         OR LOWER(dt."TitleNoSpaces") = LOWER({{filterNoSpaces}})
                                         OR ({{hasRomajiVariant}} AND (LOWER(dt."Title") = {{romajiFilter}} OR LOWER(dt."TitleNoSpaces") = {{romajiFilterNoSpaces}}))
                                  ),
                                  fuzzy_title_matches AS (
                                      SELECT dt."DeckId",
                                             1 AS match_priority,
                                             pgroonga_score(dt.tableoid, dt.ctid) AS score,
                                             dt."TitleType",
                                             LENGTH(dt."Title") AS title_length
                                      FROM jiten."DeckTitles" dt
                                      WHERE (dt."Title" &@~ {{originalFilter}} OR ({{hasRomajiVariant}} AND dt."Title" &@~ {{romajiFilter}}))
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
                                        AND (dt."TitleNoSpaces" &@~ {{filterNoSpaces}} OR ({{hasRomajiVariant}} AND dt."TitleNoSpaces" &@~ {{romajiFilterNoSpaces}}))
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
                                  ORDER BY r.best_match ASC, r.length_ratio DESC, r.best_type ASC, r.best_score DESC
                                  LIMIT {{limit}}
                                  """;

        var results = await context.Database.SqlQuery<DeckIdWithCount>(sql).ToListAsync();

        if (results.Count == 0)
            results = await LevenshteinSuggestionsFallback(originalFilter, filterNoSpaces, limit);

        if (results.Count == 0)
            return Ok(new MediaSuggestionsResponse());

        var totalCount = results[0].TotalCount;
        var orderedDeckIds = results.Select(r => r.DeckId).ToList();

        var decks = await context.Decks
                                 .AsNoTracking()
                                 .Where(d => orderedDeckIds.Contains(d.DeckId))
                                 .Select(d => new MediaSuggestionDto
                                              {
                                                  DeckId = d.DeckId, OriginalTitle = d.OriginalTitle, RomajiTitle = d.RomajiTitle,
                                                  EnglishTitle = d.EnglishTitle, MediaType = d.MediaType, CoverName = d.CoverName
                                              })
                                 .ToListAsync();

        // Preserve PGroonga ordering
        var deckMap = decks.ToDictionary(d => d.DeckId);
        var suggestions = orderedDeckIds
                          .Where(id => deckMap.ContainsKey(id))
                          .Select(id => deckMap[id])
                          .ToList();

        return Ok(new MediaSuggestionsResponse { Suggestions = suggestions, TotalCount = totalCount });
    }

    /// <summary>
    /// Applies the SQL-expressible browse filters (media type, numeric ranges, genre/tag include/exclude,
    /// exclude-sequels) to a primary-deck query. Shared by the deck list and the filter-facet counts so both
    /// stay in sync. Does not apply title search, coverage, status or ignored-deck filters.
    /// </summary>
    private static IQueryable<Deck> ApplyBrowseFilters(IQueryable<Deck> query, MediaType? mediaType,
                                                       int? charCountMin, int? charCountMax,
                                                       float? difficultyMin, float? difficultyMax,
                                                       int? releaseYearMin, int? releaseYearMax,
                                                       int? uniqueKanjiMin, int? uniqueKanjiMax,
                                                       int? subdeckCountMin, int? subdeckCountMax,
                                                       int? extRatingMin, int? extRatingMax,
                                                       float? speechSpeedMin, float? speechSpeedMax,
                                                       int? speechDurationMin, int? speechDurationMax,
                                                       string? genres, string? excludeGenres,
                                                       string? tags, string? excludeTags,
                                                       bool? excludeSequels)
    {
        if (mediaType != null)
            query = query.Where(d => d.MediaType == mediaType);

        if (charCountMin != null)
            query = query.Where(d => d.CharacterCount >= charCountMin);

        if (charCountMax != null)
            query = query.Where(d => d.CharacterCount <= charCountMax);

        if (difficultyMin != null)
            query = query.Where(d => (d.DifficultyOverride > -1 ? d.DifficultyOverride : d.Difficulty)
                + (float)(d.DeckDifficulty != null ? d.DeckDifficulty.UserAdjustment : 0) >= difficultyMin);

        if (difficultyMax != null)
            query = query.Where(d => (d.DifficultyOverride > -1 ? d.DifficultyOverride : d.Difficulty)
                + (float)(d.DeckDifficulty != null ? d.DeckDifficulty.UserAdjustment : 0) <= difficultyMax);

        if (releaseYearMin != null)
            query = query.Where(d => d.ReleaseDate.Year >= releaseYearMin);

        if (releaseYearMax != null)
            query = query.Where(d => d.ReleaseDate.Year <= releaseYearMax);

        if (uniqueKanjiMin != null)
            query = query.Where(d => d.UniqueKanjiCount >= uniqueKanjiMin);

        if (uniqueKanjiMax != null)
            query = query.Where(d => d.UniqueKanjiCount <= uniqueKanjiMax);

        if (subdeckCountMin != null)
            query = query.Where(d => d.Children.Count >= subdeckCountMin);

        if (subdeckCountMax != null)
            query = query.Where(d => d.Children.Count <= subdeckCountMax);

        if (extRatingMin != null)
            query = query.Where(d => d.ExternalRating >= extRatingMin);

        if (extRatingMax != null)
            query = query.Where(d => d.ExternalRating <= extRatingMax);

        if (speechSpeedMin != null || speechSpeedMax != null)
        {
            query = query.Where(d => d.SpeechDuration > 0);

            if (speechSpeedMin != null)
                query = query.Where(d => d.SpeechMoraCount / (d.SpeechDuration / 60000.0) >= speechSpeedMin);

            if (speechSpeedMax != null)
                query = query.Where(d => d.SpeechMoraCount / (d.SpeechDuration / 60000.0) <= speechSpeedMax);
        }

        if (speechDurationMin != null)
            query = query.Where(d => d.SpeechDuration >= (long)speechDurationMin * 3_600_000L);

        if (speechDurationMax != null)
            query = query.Where(d => d.SpeechDuration <= (long)speechDurationMax * 3_600_000L);

        if (!string.IsNullOrEmpty(genres))
        {
            var genreIds = genres.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                 .Select(g => int.TryParse(g, out var genreId) ? (Genre?)genreId : null)
                                 .Where(g => g.HasValue)
                                 .Select(g => g!.Value)
                                 .ToList();

            if (genreIds.Any())
            {
                foreach (var genreId in genreIds)
                {
                    query = query.Where(d => d.DeckGenres.Any(dg => dg.Genre == genreId));
                }
            }
        }

        if (!string.IsNullOrEmpty(excludeGenres))
        {
            var excludeGenreIds = excludeGenres.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                               .Select(g => int.TryParse(g, out var genreId) ? (Genre?)genreId : null)
                                               .Where(g => g.HasValue)
                                               .Select(g => g!.Value)
                                               .ToList();

            if (excludeGenreIds.Any())
            {
                query = query.Where(d => !d.DeckGenres.Any(dg => excludeGenreIds.Contains(dg.Genre)));
            }
        }

        if (!string.IsNullOrEmpty(tags))
        {
            var tagIds = tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                             .Select(t => int.TryParse(t, out var tagId) ? (int?)tagId : null)
                             .Where(t => t.HasValue)
                             .Select(t => t!.Value)
                             .ToList();

            if (tagIds.Any())
            {
                foreach (var tagId in tagIds)
                {
                    query = query.Where(d => d.DeckTags.Any(dt => dt.TagId == tagId));
                }
            }
        }

        if (!string.IsNullOrEmpty(excludeTags))
        {
            var excludeTagIds = excludeTags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                           .Select(t => int.TryParse(t, out var tagId) ? (int?)tagId : null)
                                           .Where(t => t.HasValue)
                                           .Select(t => t!.Value)
                                           .ToList();

            if (excludeTagIds.Any())
            {
                query = query.Where(d => !d.DeckTags.Any(dt => excludeTagIds.Contains(dt.TagId)));
            }
        }

        if (excludeSequels == true)
        {
            query = query.Where(d =>
                                    !d.RelationshipsAsSource.Any(r =>
                                                                     r.RelationshipType == DeckRelationshipType.Sequel ||
                                                                     r.RelationshipType == DeckRelationshipType.Fandisc));
        }

        return query;
    }

    /// <summary>
    /// Per-genre and per-tag deck counts for the current browse filter selection, so the filter UI can annotate
    /// each chip with how many matching decks carry it. Mirrors the deck list's SQL-expressible filters (it does
    /// not apply title search, coverage, status or ignored-deck filters, which are request- or user-specific).
    /// </summary>
    [HttpGet("filter-facets")]
    [ResponseCache(Duration = 300, VaryByQueryKeys =
                   [
                       "mediaType", "charCountMin", "charCountMax", "difficultyMin", "difficultyMax", "releaseYearMin",
                       "releaseYearMax", "uniqueKanjiMin", "uniqueKanjiMax", "subdeckCountMin", "subdeckCountMax",
                       "extRatingMin", "extRatingMax", "genres", "excludeGenres", "tags", "excludeTags", "speechSpeedMin",
                       "speechSpeedMax", "speechDurationMin", "speechDurationMax", "excludeSequels"
                   ])]
    [SwaggerOperation(Summary = "Browse filter facet counts",
                      Description = "Returns per-genre and per-tag deck counts for the current filter selection.")]
    public async Task<IResult> GetFilterFacets(MediaType? mediaType = null,
                                               int? charCountMin = null, int? charCountMax = null,
                                               float? difficultyMin = null, float? difficultyMax = null,
                                               int? releaseYearMin = null, int? releaseYearMax = null,
                                               int? uniqueKanjiMin = null, int? uniqueKanjiMax = null,
                                               int? subdeckCountMin = null, int? subdeckCountMax = null,
                                               int? extRatingMin = null, int? extRatingMax = null,
                                               string? genres = null, string? excludeGenres = null,
                                               string? tags = null, string? excludeTags = null,
                                               float? speechSpeedMin = null, float? speechSpeedMax = null,
                                               int? speechDurationMin = null, int? speechDurationMax = null,
                                               bool? excludeSequels = null)
    {
        var query = ApplyBrowseFilters(context.Decks.AsNoTracking().Where(d => d.ParentDeckId == null), mediaType,
                                       charCountMin, charCountMax, difficultyMin, difficultyMax, releaseYearMin,
                                       releaseYearMax, uniqueKanjiMin, uniqueKanjiMax, subdeckCountMin, subdeckCountMax,
                                       extRatingMin, extRatingMax, speechSpeedMin, speechSpeedMax, speechDurationMin,
                                       speechDurationMax, genres, excludeGenres, tags, excludeTags, excludeSequels);

        var genreCounts = await query.SelectMany(d => d.DeckGenres)
                                     .GroupBy(dg => dg.Genre)
                                     .Select(g => new { g.Key, Count = g.Count() })
                                     .ToDictionaryAsync(g => (int)g.Key, g => g.Count);

        var tagCounts = await query.SelectMany(d => d.DeckTags)
                                   .GroupBy(dt => dt.TagId)
                                   .Select(g => new { g.Key, Count = g.Count() })
                                   .ToDictionaryAsync(g => g.Key, g => g.Count);

        return Results.Ok(new { genreCounts, tagCounts });
    }

    /// <summary>
    /// Returns media decks with optional filtering, sorting and pagination.
    /// </summary>
    /// <param name="offset">Page offset (multiple of 50).</param>
    /// <param name="mediaType">Restrict to a specific media type.</param>
    /// <param name="wordId">If set, only decks containing this word are returned.</param>
    /// <param name="readingIndex">Reading index associated with wordId.</param>
    /// <param name="titleFilter">Full‑text filter on title (supports romaji/english/japanese).</param>
    /// <param name="sortBy">Sort field (title, difficulty, charCount, wordCount, sentenceLength, dialoguePercentage, subtitleRate, uKanji, uWordCount, uKanjiOnce, filter, releaseDate, coverage, uCoverage, totalCoverage, uTotalCoverage, communityVotes, popularity, etc.).</param>
    /// <param name="sortOrder">Ascending or Descending.</param>
    /// <param name="status">Status (none, nostatus, ignore, planning, ongoing, completed, dropped; "fav" is a legacy alias for favourite=true)</param>
    /// <param name="favourite">If true, only decks the user has favourited are returned.</param>
    /// <returns>Paginated list of decks.</returns>
    [HttpGet("get-media-decks")]
    [ResponseCache(Duration = 300, VaryByHeader = "Authorization",
                   VaryByQueryKeys =
                   [
                       "offset", "mediaType", "wordId", "readingIndex", "titleFilter", "sortBy", "sortOrder", "status", "favourite",
                       "charCountMin", "charCountMax", "difficultyMin", "difficultyMax", "releaseYearMin", "releaseYearMax",
                       "uniqueKanjiMin",
                       "uniqueKanjiMax", "subdeckCountMin", "subdeckCountMax", "extRatingMin", "extRatingMax", "genres",
                       "excludeGenres", "tags", "excludeTags", "coverageMin", "coverageMax", "uniqueCoverageMin",
                       "uniqueCoverageMax", "totalCoverageMin", "totalCoverageMax", "uTotalCoverageMin", "uTotalCoverageMax",
                       "speechSpeedMin", "speechSpeedMax"
                   ])]
    [SwaggerOperation(Summary = "List media decks",
                      Description =
                          "Returns a paginated list of decks with optional filters, sorting and user coverage when authenticated.")]
    [ProducesResponseType(typeof(PaginatedResponse<List<DeckDto>>), StatusCodes.Status200OK)]
    public async Task<PaginatedResponse<List<DeckDto>>> GetMediaDecks(int? offset = 0, MediaType? mediaType = null,
                                                                      int wordId = 0, int readingIndex = 0, string? titleFilter = "",
                                                                      string? sortBy = "",
                                                                      SortOrder? sortOrder = null,
                                                                      string status = "",
                                                                      int? charCountMin = null, int? charCountMax = null,
                                                                      float? difficultyMin = null, float? difficultyMax = null,
                                                                      int? releaseYearMin = null, int? releaseYearMax = null,
                                                                      int? uniqueKanjiMin = null, int? uniqueKanjiMax = null,
                                                                      int? subdeckCountMin = null, int? subdeckCountMax = null,
                                                                      int? extRatingMin = null, int? extRatingMax = null,
                                                                      string? genres = null, string? excludeGenres = null,
                                                                      string? tags = null, string? excludeTags = null,
                                                                      float? coverageMin = null, float? coverageMax = null,
                                                                      float? uniqueCoverageMin = null, float? uniqueCoverageMax = null,
                                                                      float? totalCoverageMin = null, float? totalCoverageMax = null,
                                                                      float? uTotalCoverageMin = null, float? uTotalCoverageMax = null,
                                                                      float? speechSpeedMin = null, float? speechSpeedMax = null,
                                                                      int? speechDurationMin = null, int? speechDurationMax = null,
                                                                      bool? excludeSequels = null, bool? favourite = null)
    {
        // Responses carry the viewer's coverage and preferences; they must not be shared from a cache.
        if (currentUserService.IsAuthenticated)
        {
            Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        }

        int pageSize = 50;
        var query = context.Decks.AsNoTracking();

        // Use "Search then Load" pattern to preserve PGroonga ordering
        List<int>? orderedDeckIds = null;

        if (!string.IsNullOrEmpty(titleFilter))
        {
            var originalFilter = titleFilter.Trim();
            var romajiFilter = TextNormalizationHelper.ContainsRomaji(originalFilter)
                ? TextNormalizationHelper.NormaliseRomaji(originalFilter)
                : originalFilter;
            var hasRomajiVariant = romajiFilter != originalFilter.ToLowerInvariant();
            var filterNoSpaces = originalFilter.Replace(" ", "");
            var romajiFilterNoSpaces = romajiFilter.Replace(" ", "");
            var queryLength = originalFilter.Length;

            FormattableString sql = $$"""
                                      WITH exact_matches AS (
                                          SELECT DISTINCT dt."DeckId",
                                                 0 AS match_priority,
                                                 100.0 AS score,
                                                 dt."TitleType",
                                                 LENGTH(dt."Title") AS title_length
                                          FROM jiten."DeckTitles" dt
                                          WHERE LOWER(dt."Title") = LOWER({{originalFilter}})
                                             OR LOWER(dt."TitleNoSpaces") = LOWER({{filterNoSpaces}})
                                             OR ({{hasRomajiVariant}} AND (LOWER(dt."Title") = {{romajiFilter}} OR LOWER(dt."TitleNoSpaces") = {{romajiFilterNoSpaces}}))
                                      ),
                                      fuzzy_title_matches AS (
                                          SELECT dt."DeckId",
                                                 1 AS match_priority,
                                                 pgroonga_score(dt.tableoid, dt.ctid) AS score,
                                                 dt."TitleType",
                                                 LENGTH(dt."Title") AS title_length
                                          FROM jiten."DeckTitles" dt
                                          WHERE (dt."Title" &@~ {{originalFilter}} OR ({{hasRomajiVariant}} AND dt."Title" &@~ {{romajiFilter}}))
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
                                            AND (dt."TitleNoSpaces" &@~ {{filterNoSpaces}} OR ({{hasRomajiVariant}} AND dt."TitleNoSpaces" &@~ {{romajiFilterNoSpaces}}))
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
                                      SELECT r."DeckId"
                                      FROM ranked r
                                      JOIN jiten."Decks" d ON r."DeckId" = d."DeckId"
                                      WHERE d."ParentDeckId" IS NULL
                                      ORDER BY r.best_match ASC, r.length_ratio DESC, r.best_type ASC, r.best_score DESC
                                      """;

            orderedDeckIds = await context.Database.SqlQuery<int>(sql).ToListAsync();

            if (orderedDeckIds.Count == 0)
                orderedDeckIds = await LevenshteinDeckIdsFallback(originalFilter, filterNoSpaces);

            query = query.Where(d => orderedDeckIds.Contains(d.DeckId));
        }
        else
        {
            query = query.Where(d => d.ParentDeckId == null);
        }

        query = ApplyBrowseFilters(query, mediaType, charCountMin, charCountMax, difficultyMin, difficultyMax,
                                   releaseYearMin, releaseYearMax, uniqueKanjiMin, uniqueKanjiMax, subdeckCountMin,
                                   subdeckCountMax, extRatingMin, extRatingMax, speechSpeedMin, speechSpeedMax,
                                   speechDurationMin, speechDurationMax, genres, excludeGenres, tags, excludeTags,
                                   excludeSequels);

        if (wordId != 0)
        {
            query = query.Where(d => context.DeckWords
                                            .Any(dw => dw.DeckId == d.DeckId && dw.WordId == wordId && dw.ReadingIndex == readingIndex));
        }

        Dictionary<int, UserDeckPreference> allUserPrefs = new();
        HashSet<int> favDeckIds = new();
        HashSet<int> ignoredDeckIds = new();

        if (currentUserService.IsAuthenticated)
        {
            var userId = currentUserService.UserId!;
            var prefsList = await userContext.UserDeckPreferences
                                             .AsNoTracking()
                                             .Where(p => p.UserId == userId)
                                             .ToListAsync();

            allUserPrefs = prefsList.ToDictionary(p => p.DeckId);
            favDeckIds = prefsList.Where(p => p.IsFavourite).Select(p => p.DeckId).ToHashSet();
            ignoredDeckIds = prefsList.Where(p => p.IsIgnored).Select(p => p.DeckId).ToHashSet();
        }

        // Legacy alias: "fav" predates the standalone favourite flag and must keep working for old URLs and presets.
        var normalizedStatus = status?.ToLowerInvariant() ?? "";
        if (normalizedStatus == "fav")
        {
            favourite = true;
            normalizedStatus = "none";
        }

        if (currentUserService.IsAuthenticated && favourite == true)
        {
            query = query.Where(d => favDeckIds.Contains(d.DeckId));
        }

        if (currentUserService.IsAuthenticated && normalizedStatus.Length > 0)
        {
            if (normalizedStatus == "ignore")
            {
                query = query.Where(d => ignoredDeckIds.Contains(d.DeckId));
            }
            else if (normalizedStatus == "nostatus")
            {
                var decksWithStatus = allUserPrefs
                                      .Where(p => p.Value.Status != DeckStatus.None)
                                      .Select(p => p.Key)
                                      .ToHashSet();
                query = query.Where(d => !decksWithStatus.Contains(d.DeckId));
            }
            else if (normalizedStatus != "none")
            {
                DeckStatus? deckStatus = normalizedStatus switch
                {
                    "planning" => DeckStatus.Planning,
                    "ongoing" => DeckStatus.Ongoing,
                    "completed" => DeckStatus.Completed,
                    "dropped" => DeckStatus.Dropped,
                    _ => null
                };

                if (deckStatus.HasValue)
                {
                    var statusDeckIds = allUserPrefs
                                        .Where(p => p.Value.Status == deckStatus.Value)
                                        .Select(p => p.Key)
                                        .ToHashSet();
                    query = query.Where(d => statusDeckIds.Contains(d.DeckId));
                }
            }
        }

        if (currentUserService.IsAuthenticated && normalizedStatus != "ignore")
        {
            query = query.Where(d => !ignoredDeckIds.Contains(d.DeckId));
        }

        query = query.Include(d => d.Children)
                     .Include(d => d.Links)
                     .Include(d => d.Titles)
                     .Include(d => d.DeckGenres)
                     .Include(d => d.DeckTags)
                     .ThenInclude(dt => dt.Tag)
                     .Include(d => d.DeckDifficulty)
                     .Include(d => d.RelationshipsAsSource)
                     .ThenInclude(r => r.TargetDeck)
                     .Include(d => d.RelationshipsAsTarget)
                     .ThenInclude(r => r.SourceDeck);


        IQueryable<DeckWithOccurrences>? projectedQuery = null;
        if (wordId != 0)
        {
            projectedQuery = query.Select(d => new DeckWithOccurrences
                                               {
                                                   Deck = d, Occurrences = d.DeckWords
                                                                            .Where(dw => dw.WordId == wordId &&
                                                                                         dw.ReadingIndex == readingIndex)
                                                                            .Select(dw => (int?)dw.Occurrences)
                                                                            .FirstOrDefault() ?? 0
                                               });
        }

        if (string.IsNullOrEmpty(sortBy))
            sortBy = string.IsNullOrEmpty(titleFilter) ? "popularity" : "filter";
        var order = sortOrder ?? (sortBy is "popularity" or "filter" ? SortOrder.Descending : SortOrder.Ascending);

        Dictionary<int, float> coverageDict = new();
        Dictionary<int, float> uniqueCoverageDict = new();
        Dictionary<int, float> youngCoverageDict = new();
        Dictionary<int, float> youngUniqueCoverageDict = new();

        if (currentUserService.IsAuthenticated)
        {
            var allDeckIds = await query.OrderBy(d => d.DeckId).Select(d => d.DeckId).ToListAsync();
            var userId = currentUserService.UserId!;

            var coverages = await UserCoverageChunkHelper.GetCoverage(userContext, userId, allDeckIds);
            coverageDict = coverages.MatureCoverage;
            uniqueCoverageDict = coverages.MatureUniqueCoverage;
            youngCoverageDict = coverages.YoungCoverage;
            youngUniqueCoverageDict = coverages.YoungUniqueCoverage;

            var totalCoverageDict = CombineCoverage(coverageDict, youngCoverageDict);
            var uniqueTotalCoverageDict = CombineCoverage(uniqueCoverageDict, youngUniqueCoverageDict);

            if (coverageMin != null || coverageMax != null)
            {
                var matchingIds = coverageDict
                                  .Where(kvp => (coverageMin == null || kvp.Value >= coverageMin) &&
                                                (coverageMax == null || kvp.Value <= coverageMax))
                                  .Select(kvp => kvp.Key)
                                  .ToHashSet();
                query = query.Where(d => matchingIds.Contains(d.DeckId));
            }

            if (uniqueCoverageMin != null || uniqueCoverageMax != null)
            {
                var matchingIds = uniqueCoverageDict
                                  .Where(kvp => (uniqueCoverageMin == null || kvp.Value >= uniqueCoverageMin) &&
                                                (uniqueCoverageMax == null || kvp.Value <= uniqueCoverageMax))
                                  .Select(kvp => kvp.Key)
                                  .ToHashSet();
                query = query.Where(d => matchingIds.Contains(d.DeckId));
            }

            if (totalCoverageMin != null || totalCoverageMax != null)
            {
                var matchingIds = totalCoverageDict
                                  .Where(kvp => (totalCoverageMin == null || kvp.Value >= totalCoverageMin) &&
                                                (totalCoverageMax == null || kvp.Value <= totalCoverageMax))
                                  .Select(kvp => kvp.Key)
                                  .ToHashSet();
                query = query.Where(d => matchingIds.Contains(d.DeckId));
            }

            if (uTotalCoverageMin != null || uTotalCoverageMax != null)
            {
                var matchingIds = uniqueTotalCoverageDict
                                  .Where(kvp => (uTotalCoverageMin == null || kvp.Value >= uTotalCoverageMin) &&
                                                (uTotalCoverageMax == null || kvp.Value <= uTotalCoverageMax))
                                  .Select(kvp => kvp.Key)
                                  .ToHashSet();
                query = query.Where(d => matchingIds.Contains(d.DeckId));
            }


            if (sortBy is "coverage" or "uCoverage" or "totalCoverage" or "uTotalCoverage")
            {
                var sortDict = sortBy switch
                {
                    "uCoverage" => uniqueCoverageDict,
                    "totalCoverage" => totalCoverageDict,
                    "uTotalCoverage" => uniqueTotalCoverageDict,
                    _ => coverageDict
                };

                return await HandleCoverageSorting(query, projectedQuery, order, offset ?? 0, pageSize, coverageDict,
                                                   uniqueCoverageDict, youngCoverageDict, youngUniqueCoverageDict, sortDict,
                                                   allUserPrefs);
            }
        }

        if (wordId != 0)
        {
            return await HandleWordBasedQuery(projectedQuery!, wordId, readingIndex, sortBy, order, offset ?? 0, pageSize, coverageDict,
                                              uniqueCoverageDict, youngCoverageDict, youngUniqueCoverageDict, allUserPrefs, orderedDeckIds);
        }

        query = ApplySorting(query, sortBy, order);
        var totalCount = await query.CountAsync();

        List<Deck> paginatedDecks;
        if (orderedDeckIds is { Count: > 0 } && sortBy == "filter")
        {
            var filteredIdsSet = (await query.Select(d => d.DeckId).ToListAsync()).ToHashSet();

            var filteredOrderedIds = orderedDeckIds.Where(id => filteredIdsSet.Contains(id)).ToList();

            var paginatedIds = filteredOrderedIds.Skip(offset ?? 0).Take(pageSize).ToList();

            var filteredQuery = query.Where(d => paginatedIds.Contains(d.DeckId));
            var unorderedDecks = await filteredQuery.AsSplitQuery().ToListAsync();

            var deckLookup = unorderedDecks.ToDictionary(d => d.DeckId);
            paginatedDecks = paginatedIds
                             .Where(id => deckLookup.ContainsKey(id))
                             .Select(id => deckLookup[id])
                             .ToList();
        }
        else
        {
            paginatedDecks = await query
                                   .Skip(offset ?? 0)
                                   .Take(pageSize)
                                   .AsSplitQuery()
                                   .ToListAsync();
        }

        var dtos = paginatedDecks.Select(deck => new DeckDto(deck)).ToList();

        foreach (var (dto, deck) in dtos.Zip(paginatedDecks))
            dto.Relationships = DeckRelationshipDto.FromDeck(deck.RelationshipsAsSource, deck.RelationshipsAsTarget);

        if (currentUserService.IsAuthenticated)
        {
            foreach (var dto in dtos)
            {
                if (coverageDict.TryGetValue(dto.DeckId, out var c)) dto.Coverage = c;
                if (uniqueCoverageDict.TryGetValue(dto.DeckId, out var uc)) dto.UniqueCoverage = uc;
                if (youngCoverageDict.TryGetValue(dto.DeckId, out var yc)) dto.YoungCoverage = yc;
                if (youngUniqueCoverageDict.TryGetValue(dto.DeckId, out var yuc)) dto.YoungUniqueCoverage = yuc;
                if (allUserPrefs.TryGetValue(dto.DeckId, out var pref))
                {
                    dto.Status = pref.Status;
                    dto.IsFavourite = pref.IsFavourite;
                    dto.IsIgnored = pref.IsIgnored;
                }
            }
        }

        return new PaginatedResponse<List<DeckDto>>(dtos, totalCount, pageSize, offset ?? 0);
    }

    /// <summary>
    /// Mature + young coverage, capped at 100% to match the total shown on the deck cards.
    /// </summary>
    private static Dictionary<int, float> CombineCoverage(Dictionary<int, float> mature, Dictionary<int, float> young)
    {
        var combined = new Dictionary<int, float>(mature);

        foreach (var (deckId, youngValue) in young)
            combined[deckId] = Math.Min(combined.GetValueOrDefault(deckId) + youngValue, 100f);

        return combined;
    }

    private async Task<PaginatedResponse<List<DeckDto>>> HandleCoverageSorting(
        IQueryable<Deck> query,
        IQueryable<DeckWithOccurrences>? projectedQuery,
        SortOrder sortOrder,
        int offset,
        int pageSize,
        Dictionary<int, float> coverageDict,
        Dictionary<int, float> uniqueCoverageDict,
        Dictionary<int, float> youngCoverageDict,
        Dictionary<int, float> youngUniqueCoverageDict,
        Dictionary<int, float> selectedDict,
        Dictionary<int, UserDeckPreference> preferencesDict)
    {
        var totalCount = await query.CountAsync();
        var allDeckIds = await query.Select(d => d.DeckId).ToListAsync();

        var idsWithCoverage = allDeckIds.Where(id => selectedDict.ContainsKey(id)).ToList();
        var idsWithoutCoverage = allDeckIds.Where(id => !selectedDict.ContainsKey(id)).ToList();

        IEnumerable<int> orderedWithCoverage = sortOrder == SortOrder.Ascending
            ? idsWithCoverage.OrderBy(id => selectedDict[id])
            : idsWithCoverage.OrderByDescending(id => selectedDict[id]);

        var orderedIds = orderedWithCoverage.Concat(idsWithoutCoverage).ToList();
        var pagedIds = orderedIds.Skip(offset).Take(pageSize).ToList();

        if (projectedQuery != null)
        {
            var paginatedProjections = await projectedQuery
                                             .Where(p => pagedIds.Contains(p.Deck.DeckId))
                                             .ToListAsync();

            var deckIdsToHydrate = paginatedProjections.Select(p => p.Deck.DeckId).ToList();

            var fullDecks = await context.Decks.AsNoTracking()
                                         .Where(d => deckIdsToHydrate.Contains(d.DeckId))
                                         .Include(d => d.Children)
                                         .Include(d => d.Links)
                                         .Include(d => d.Titles)
                                         .Include(d => d.DeckGenres)
                                         .Include(d => d.DeckTags)
                                         .ThenInclude(dt => dt.Tag)
                                         .Include(d => d.DeckDifficulty)
                                         .Include(d => d.RelationshipsAsSource)
                                         .ThenInclude(r => r.TargetDeck)
                                         .Include(d => d.RelationshipsAsTarget)
                                         .ThenInclude(r => r.SourceDeck)
                                         .AsSplitQuery()
                                         .ToListAsync();

            var fullDeckMap = fullDecks.ToDictionary(d => d.DeckId);

            var orderIndex = pagedIds.Select((id, idx) => new { id, idx }).ToDictionary(k => k.id, v => v.idx);
            var paginatedResults = paginatedProjections
                                   .Where(p => fullDeckMap.ContainsKey(p.Deck.DeckId))
                                   .Select(p => new DeckWithOccurrences { Deck = fullDeckMap[p.Deck.DeckId], Occurrences = p.Occurrences })
                                   .OrderBy(r => orderIndex[r.Deck.DeckId])
                                   .ToList();

            var dtos = paginatedResults.Select(r => new DeckDto(r.Deck, r.Occurrences)).ToList();

            foreach (var (dto, result) in dtos.Zip(paginatedResults))
                dto.Relationships = DeckRelationshipDto.FromDeck(result.Deck.RelationshipsAsSource, result.Deck.RelationshipsAsTarget);

            foreach (var dto in dtos)
            {
                if (currentUserService.IsAuthenticated)
                {
                    if (preferencesDict.TryGetValue(dto.DeckId, out var pref))
                    {
                        dto.Status = pref.Status;
                        dto.IsFavourite = pref.IsFavourite;
                        dto.IsIgnored = pref.IsIgnored;
                    }
                }

                if (coverageDict.TryGetValue(dto.DeckId, out var cov)) dto.Coverage = cov;
                if (uniqueCoverageDict.TryGetValue(dto.DeckId, out var uCov)) dto.UniqueCoverage = uCov;
                if (youngCoverageDict.TryGetValue(dto.DeckId, out var yCov)) dto.YoungCoverage = yCov;
                if (youngUniqueCoverageDict.TryGetValue(dto.DeckId, out var yuCov)) dto.YoungUniqueCoverage = yuCov;
            }

            return new PaginatedResponse<List<DeckDto>>(dtos, totalCount, pageSize, offset);
        }
        else
        {
            var pagedDecks = await query
                                   .Where(d => pagedIds.Contains(d.DeckId))
                                   .ToListAsync();

            var orderIndex = pagedIds.Select((id, idx) => new { id, idx }).ToDictionary(k => k.id, v => v.idx);
            pagedDecks = pagedDecks.OrderBy(d => orderIndex[d.DeckId]).ToList();

            var dtos = pagedDecks.Select(deck => new DeckDto(deck)).ToList();

            foreach (var (dto, deck) in dtos.Zip(pagedDecks))
                dto.Relationships = DeckRelationshipDto.FromDeck(deck.RelationshipsAsSource, deck.RelationshipsAsTarget);

            foreach (var dto in dtos)
            {
                if (currentUserService.IsAuthenticated)
                {
                    if (preferencesDict.TryGetValue(dto.DeckId, out var pref))
                    {
                        dto.Status = pref.Status;
                        dto.IsFavourite = pref.IsFavourite;
                        dto.IsIgnored = pref.IsIgnored;
                    }
                }

                if (coverageDict.TryGetValue(dto.DeckId, out var cov)) dto.Coverage = cov;
                if (uniqueCoverageDict.TryGetValue(dto.DeckId, out var uCov)) dto.UniqueCoverage = uCov;
                if (youngCoverageDict.TryGetValue(dto.DeckId, out var yCov)) dto.YoungCoverage = yCov;
                if (youngUniqueCoverageDict.TryGetValue(dto.DeckId, out var yuCov)) dto.YoungUniqueCoverage = yuCov;
            }

            return new PaginatedResponse<List<DeckDto>>(dtos, totalCount, pageSize, offset);
        }
    }

    private IQueryable<Deck> ApplySorting(IQueryable<Deck> query, string sortBy, SortOrder sortOrder)
    {
        return sortBy switch
        {
            "difficulty" => sortOrder == SortOrder.Ascending
                ? query.Where(d => d.Difficulty > -1)
                       .OrderBy(d => (d.DifficultyOverride > -1 ? d.DifficultyOverride : d.Difficulty)
                           + (float)(d.DeckDifficulty != null ? d.DeckDifficulty.UserAdjustment : 0))
                       .ThenBy(d => d.DeckId)
                : query.Where(d => d.Difficulty > -1)
                       .OrderByDescending(d => (d.DifficultyOverride > -1 ? d.DifficultyOverride : d.Difficulty)
                           + (float)(d.DeckDifficulty != null ? d.DeckDifficulty.UserAdjustment : 0))
                       .ThenBy(d => d.DeckId),
            "charCount" => sortOrder == SortOrder.Ascending
                ? query.Where(d => d.MediaType != MediaType.Anime && d.MediaType != MediaType.Drama && d.MediaType != MediaType.Movie && d.MediaType != MediaType.Audio)
                       .OrderBy(d => d.CharacterCount)
                       .ThenBy(d => d.DeckId)
                : query.Where(d => d.MediaType != MediaType.Anime && d.MediaType != MediaType.Drama && d.MediaType != MediaType.Movie && d.MediaType != MediaType.Audio)
                       .OrderByDescending(d => d.CharacterCount)
                       .ThenBy(d => d.DeckId),
            "sentenceLength" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.CharacterCount / (d.SentenceCount + 1)).ThenBy(d => d.DeckId).Where(d => d.SentenceCount != 0 && !d.HideAverageSentenceLength)
                : query.OrderByDescending(d => d.CharacterCount / (d.SentenceCount + 1)).ThenBy(d => d.DeckId).Where(d => d.SentenceCount != 0 && !d.HideAverageSentenceLength),
            "dialoguePercentage" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.DialoguePercentage)
                       .ThenBy(d => d.DeckId)
                       .Where(d => !d.HideDialoguePercentage && d.DialoguePercentage != 0 && d.DialoguePercentage != 100)
                : query.OrderByDescending(d => d.DialoguePercentage)
                       .ThenBy(d => d.DeckId)
                       .Where(d => !d.HideDialoguePercentage && d.DialoguePercentage != 0 && d.DialoguePercentage != 100),
            "speechSpeed" => sortOrder == SortOrder.Ascending
                ? query.Where(d => d.SpeechDuration > 0)
                       .OrderBy(d => d.SpeechMoraCount / (d.SpeechDuration / 60000.0))
                       .ThenBy(d => d.DeckId)
                : query.Where(d => d.SpeechDuration > 0)
                       .OrderByDescending(d => d.SpeechMoraCount / (d.SpeechDuration / 60000.0))
                       .ThenBy(d => d.DeckId),
            "speechDuration" => sortOrder == SortOrder.Ascending
                ? query.Where(d => d.SpeechDuration > 0)
                       .OrderBy(d => d.SpeechDuration)
                       .ThenBy(d => d.DeckId)
                : query.Where(d => d.SpeechDuration > 0)
                       .OrderByDescending(d => d.SpeechDuration)
                       .ThenBy(d => d.DeckId),
            "wordCount" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.WordCount).ThenBy(d => d.DeckId)
                : query.OrderByDescending(d => d.WordCount).ThenBy(d => d.DeckId),
            "uKanji" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.UniqueKanjiCount).ThenBy(d => d.DeckId)
                : query.OrderByDescending(d => d.UniqueKanjiCount).ThenBy(d => d.DeckId),
            "uWordCount" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.UniqueWordCount).ThenBy(d => d.DeckId)
                : query.OrderByDescending(d => d.UniqueWordCount).ThenBy(d => d.DeckId),
            "uKanjiOnce" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.UniqueKanjiUsedOnceCount).ThenBy(d => d.DeckId)
                : query.OrderByDescending(d => d.UniqueKanjiUsedOnceCount).ThenBy(d => d.DeckId),
            "filter" => query.OrderBy(_ => 1), // Dummy ordering for pgroonga_score
            "releaseDate" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.ReleaseDate).ThenBy(d => d.DeckId)
                : query.OrderByDescending(d => d.ReleaseDate).ThenBy(d => d.DeckId),
            "addedDate" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.CreationDate).ThenBy(d => d.DeckId)
                : query.OrderByDescending(d => d.CreationDate).ThenBy(d => d.DeckId),
            "subdeckCount" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.Children.Count).ThenBy(d => d.DeckId)
                : query.OrderByDescending(d => d.Children.Count).ThenBy(d => d.DeckId),
            "extRating" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.ExternalRating)
                       .ThenBy(d => d.DeckId)
                       .Where(d => d.ExternalRating != 0)
                : query.OrderByDescending(d => d.ExternalRating)
                       .ThenBy(d => d.DeckId)
                       .Where(d => d.ExternalRating != 0),
            "communityVotes" => sortOrder == SortOrder.Ascending
                ? query.Where(d => d.DeckDifficulty != null && d.DeckDifficulty.DistinctVoterCount > 0)
                       .OrderBy(d => d.DeckDifficulty!.DistinctVoterCount)
                       .ThenBy(d => d.DeckId)
                : query.Where(d => d.DeckDifficulty != null && d.DeckDifficulty.DistinctVoterCount > 0)
                       .OrderByDescending(d => d.DeckDifficulty!.DistinctVoterCount)
                       .ThenBy(d => d.DeckId),
            "popularity" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.PopularityScore == 0).ThenBy(d => d.PopularityScore).ThenBy(d => d.ExternalRating).ThenBy(d => d.ReleaseDate).ThenBy(d => d.DeckId)
                : query.OrderByDescending(d => d.PopularityScore).ThenByDescending(d => d.ExternalRating).ThenByDescending(d => d.ReleaseDate).ThenBy(d => d.DeckId),
            _ => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.RomajiTitle).ThenBy(d => d.DeckId)
                : query.OrderByDescending(d => d.RomajiTitle).ThenBy(d => d.DeckId),
        };
    }

    private IQueryable<DeckWithOccurrences> ApplySorting(IQueryable<DeckWithOccurrences> query, string sortBy, SortOrder sortOrder)
    {
        return sortBy switch
        {
            "occurrences" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(p => p.Occurrences).ThenBy(p => p.Deck.DeckId)
                : query.OrderByDescending(p => p.Occurrences).ThenBy(p => p.Deck.DeckId),
            "difficulty" => sortOrder == SortOrder.Ascending
                ? query.Where(p => p.Deck.Difficulty > -1)
                       .OrderBy(p => (p.Deck.DifficultyOverride > -1 ? p.Deck.DifficultyOverride : p.Deck.Difficulty)
                           + (float)(p.Deck.DeckDifficulty != null ? p.Deck.DeckDifficulty.UserAdjustment : 0))
                       .ThenBy(p => p.Deck.DeckId)
                : query.Where(p => p.Deck.Difficulty > -1)
                       .OrderByDescending(p => (p.Deck.DifficultyOverride > -1 ? p.Deck.DifficultyOverride : p.Deck.Difficulty)
                           + (float)(p.Deck.DeckDifficulty != null ? p.Deck.DeckDifficulty.UserAdjustment : 0))
                       .ThenBy(p => p.Deck.DeckId),
            "charCount" => sortOrder == SortOrder.Ascending
                ? query.Where(p => p.Deck.MediaType != MediaType.Anime && p.Deck.MediaType != MediaType.Drama && p.Deck.MediaType != MediaType.Movie && p.Deck.MediaType != MediaType.Audio)
                       .OrderBy(p => p.Deck.CharacterCount)
                       .ThenBy(p => p.Deck.DeckId)
                : query.Where(p => p.Deck.MediaType != MediaType.Anime && p.Deck.MediaType != MediaType.Drama && p.Deck.MediaType != MediaType.Movie && p.Deck.MediaType != MediaType.Audio)
                       .OrderByDescending(p => p.Deck.CharacterCount)
                       .ThenBy(p => p.Deck.DeckId),
            "sentenceLength" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(p => p.Deck.CharacterCount / (p.Deck.SentenceCount + 1)).ThenBy(p => p.Deck.DeckId).Where(p => p.Deck.SentenceCount != 0 && !p.Deck.HideAverageSentenceLength)
                : query.OrderByDescending(p => p.Deck.CharacterCount / (p.Deck.SentenceCount + 1)).ThenBy(p => p.Deck.DeckId).Where(p => p.Deck.SentenceCount != 0 && !p.Deck.HideAverageSentenceLength),
            "dialoguePercentage" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(p => p.Deck.DialoguePercentage)
                       .ThenBy(p => p.Deck.DeckId)
                       .Where(p => p.Deck.DialoguePercentage != 0 && p.Deck.DialoguePercentage != 100)
                : query.OrderByDescending(p => p.Deck.DialoguePercentage)
                       .ThenBy(p => p.Deck.DeckId)
                       .Where(p => p.Deck.DialoguePercentage != 0 && p.Deck.DialoguePercentage != 100),
            "speechSpeed" => sortOrder == SortOrder.Ascending
                ? query.Where(p => p.Deck.SpeechDuration > 0)
                       .OrderBy(p => p.Deck.SpeechMoraCount / (p.Deck.SpeechDuration / 60000.0))
                       .ThenBy(p => p.Deck.DeckId)
                : query.Where(p => p.Deck.SpeechDuration > 0)
                       .OrderByDescending(p => p.Deck.SpeechMoraCount / (p.Deck.SpeechDuration / 60000.0))
                       .ThenBy(p => p.Deck.DeckId),
            "speechDuration" => sortOrder == SortOrder.Ascending
                ? query.Where(p => p.Deck.SpeechDuration > 0)
                       .OrderBy(p => p.Deck.SpeechDuration)
                       .ThenBy(p => p.Deck.DeckId)
                : query.Where(p => p.Deck.SpeechDuration > 0)
                       .OrderByDescending(p => p.Deck.SpeechDuration)
                       .ThenBy(p => p.Deck.DeckId),
            "wordCount" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(p => p.Deck.WordCount).ThenBy(p => p.Deck.DeckId)
                : query.OrderByDescending(p => p.Deck.WordCount).ThenBy(p => p.Deck.DeckId),
            "uKanji" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(p => p.Deck.UniqueKanjiCount).ThenBy(p => p.Deck.DeckId)
                : query.OrderByDescending(p => p.Deck.UniqueKanjiCount).ThenBy(p => p.Deck.DeckId),
            "uWordCount" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(p => p.Deck.UniqueWordCount).ThenBy(p => p.Deck.DeckId)
                : query.OrderByDescending(p => p.Deck.UniqueWordCount).ThenBy(p => p.Deck.DeckId),
            "uKanjiOnce" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(p => p.Deck.UniqueKanjiUsedOnceCount).ThenBy(p => p.Deck.DeckId)
                : query.OrderByDescending(p => p.Deck.UniqueKanjiUsedOnceCount).ThenBy(p => p.Deck.DeckId),
            "filter" => query.OrderBy(_ => 1), // Dummy ordering for pgroonga_score
            "releaseDate" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(p => p.Deck.ReleaseDate).ThenBy(p => p.Deck.DeckId)
                : query.OrderByDescending(p => p.Deck.ReleaseDate).ThenBy(p => p.Deck.DeckId),
            "communityVotes" => sortOrder == SortOrder.Ascending
                ? query.Where(p => p.Deck.DeckDifficulty != null && p.Deck.DeckDifficulty.DistinctVoterCount > 0)
                       .OrderBy(p => p.Deck.DeckDifficulty!.DistinctVoterCount)
                       .ThenBy(p => p.Deck.DeckId)
                : query.Where(p => p.Deck.DeckDifficulty != null && p.Deck.DeckDifficulty.DistinctVoterCount > 0)
                       .OrderByDescending(p => p.Deck.DeckDifficulty!.DistinctVoterCount)
                       .ThenBy(p => p.Deck.DeckId),
            "popularity" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(p => p.Deck.PopularityScore == 0).ThenBy(p => p.Deck.PopularityScore).ThenBy(p => p.Deck.ExternalRating).ThenBy(p => p.Deck.ReleaseDate).ThenBy(p => p.Deck.DeckId)
                : query.OrderByDescending(p => p.Deck.PopularityScore).ThenByDescending(p => p.Deck.ExternalRating).ThenByDescending(p => p.Deck.ReleaseDate).ThenBy(p => p.Deck.DeckId),
            _ => sortOrder == SortOrder.Ascending
                ? query.OrderBy(p => p.Deck.RomajiTitle).ThenBy(p => p.Deck.DeckId)
                : query.OrderByDescending(p => p.Deck.RomajiTitle).ThenBy(p => p.Deck.DeckId),
        };
    }

    private async Task<PaginatedResponse<List<DeckDto>>> HandleWordBasedQuery(
        IQueryable<DeckWithOccurrences> projectedQuery, int wordId, int readingIndex, string sortBy, SortOrder sortOrder, int offset,
        int pageSize, Dictionary<int, float> coverageDict, Dictionary<int, float> uniqueCoverageDict,
        Dictionary<int, float> youngCoverageDict, Dictionary<int, float> youngUniqueCoverageDict,
        Dictionary<int, UserDeckPreference> preferencesDict, List<int>? orderedDeckIds)
    {
        projectedQuery = ApplySorting(projectedQuery, sortBy, sortOrder);

        var totalCount = await projectedQuery.CountAsync();

        List<DeckWithOccurrences> paginatedProjections;
        List<int>? paginatedIds = null;
        if (orderedDeckIds is { Count: > 0 } && sortBy == "filter")
        {
            var filteredIdsSet = (await projectedQuery.Select(p => p.Deck.DeckId).ToListAsync()).ToHashSet();

            var filteredOrderedIds = orderedDeckIds.Where(id => filteredIdsSet.Contains(id)).ToList();

            paginatedIds = filteredOrderedIds.Skip(offset).Take(pageSize).ToList();

            var filteredQuery = projectedQuery.Where(p => paginatedIds.Contains(p.Deck.DeckId));
            paginatedProjections = await filteredQuery.AsSplitQuery().ToListAsync();
        }
        else
        {
            paginatedProjections = await projectedQuery
                                         .Skip(offset)
                                         .Take(pageSize)
                                         .AsSplitQuery()
                                         .ToListAsync();
        }

        var deckIdsToHydrate = paginatedProjections.Select(p => p.Deck.DeckId).ToList();
        var fullDecks = await context.Decks.AsNoTracking()
                                     .Where(d => deckIdsToHydrate.Contains(d.DeckId))
                                     .Include(d => d.Children)
                                     .Include(d => d.Links)
                                     .Include(d => d.Titles)
                                     .Include(d => d.DeckGenres)
                                     .Include(d => d.DeckTags)
                                     .ThenInclude(dt => dt.Tag)
                                     .Include(d => d.DeckDifficulty)
                                     .Include(d => d.RelationshipsAsSource)
                                     .ThenInclude(r => r.TargetDeck)
                                     .Include(d => d.RelationshipsAsTarget)
                                     .ThenInclude(r => r.SourceDeck)
                                     .AsSplitQuery()
                                     .ToListAsync();

        var fullDeckMap = fullDecks.ToDictionary(d => d.DeckId);

        List<DeckWithOccurrences> paginatedResults;
        if (paginatedIds is { Count: > 0 })
        {
            var projectionLookup = paginatedProjections.ToDictionary(p => p.Deck.DeckId);
            paginatedResults = paginatedIds
                               .Where(id => projectionLookup.ContainsKey(id) && fullDeckMap.ContainsKey(id))
                               .Select(id => new DeckWithOccurrences
                                             {
                                                 Deck = fullDeckMap[id], Occurrences = projectionLookup[id].Occurrences
                                             })
                               .ToList();
        }
        else
        {
            paginatedResults = paginatedProjections
                               .Where(p => fullDeckMap.ContainsKey(p.Deck.DeckId))
                               .Select(p => new DeckWithOccurrences { Deck = fullDeckMap[p.Deck.DeckId], Occurrences = p.Occurrences })
                               .ToList();
        }

        var targetDeckIds = paginatedResults.Select(r => r.Deck.DeckId).ToList();

        var minimalExamples = await context.ExampleSentences
                                           .AsNoTracking()
                                           .Join(context.Decks.AsNoTracking(),
                                                 es => es.DeckId,
                                                 d => d.DeckId,
                                                 (es, d) => new { es, d })
                                           .Where(x => targetDeckIds.Contains(x.d.ParentDeckId ?? x.d.DeckId))
                                           .Select(x => new
                                                        {
                                                            EffectiveDeckId = x.d.ParentDeckId ?? x.d.DeckId, x.es.SentenceId, x.es.Text, Match = x.es.Words
                                                                .Where(w => w.WordId == wordId && w.ReadingIndex == readingIndex)
                                                                .Select(w => new { w.Position, w.Length })
                                                                .FirstOrDefault()
                                                        })
                                           .Where(x => x.Match != null)
                                           .GroupBy(x => x.EffectiveDeckId)
                                           .Select(g => g.First())
                                           .ToListAsync();

        // Create dictionary for O(1) lookup instead of O(n) per deck
        var exampleSentencesByDeck = minimalExamples
            .ToDictionary(
                          x => x.EffectiveDeckId,
                          x => new ExampleSentenceDto { SentenceId = x.SentenceId, Text = x.Text, WordPosition = x.Match!.Position, WordLength = x.Match!.Length });

        var dtos = paginatedResults
                   .Select(r => new DeckDto(
                                            r.Deck,
                                            r.Occurrences,
                                            exampleSentencesByDeck.GetValueOrDefault(r.Deck.DeckId)))
                   .ToList();

        foreach (var (dto, result) in dtos.Zip(paginatedResults))
            dto.Relationships = DeckRelationshipDto.FromDeck(result.Deck.RelationshipsAsSource, result.Deck.RelationshipsAsTarget);

        if (currentUserService.IsAuthenticated)
        {
            foreach (var dto in dtos)
            {
                if (coverageDict.TryGetValue(dto.DeckId, out var c)) dto.Coverage = c;
                if (uniqueCoverageDict.TryGetValue(dto.DeckId, out var uc)) dto.UniqueCoverage = uc;
                if (youngCoverageDict.TryGetValue(dto.DeckId, out var yc)) dto.YoungCoverage = yc;
                if (youngUniqueCoverageDict.TryGetValue(dto.DeckId, out var yuc)) dto.YoungUniqueCoverage = yuc;
                if (preferencesDict.TryGetValue(dto.DeckId, out var pref))
                {
                    dto.Status = pref.Status;
                    dto.IsFavourite = pref.IsFavourite;
                    dto.IsIgnored = pref.IsIgnored;
                }
            }
        }

        return new PaginatedResponse<List<DeckDto>>(dtos, totalCount, pageSize, offset);
    }

    /// <summary>
    /// Returns vocabulary entries for a given deck with sorting and pagination.
    /// </summary>
    /// <param name="id">Deck identifier.</param>
    /// <param name="sortBy">Sort by globalFreq | deckFreq | chrono.</param>
    /// <param name="sortOrder">Ascending or Descending.</param>
    /// <param name="offset">Pagination offset.</param>
    /// <param name="displayFilter">When authenticated: all, or a comma-separated set of unknown | learning | young | mature | mastered | blacklisted.</param>
    /// <param name="suspended">Suspended cards: show | hide | only.</param>
    /// <param name="redundant">Redundant forms: show | hide | only.</param>
    /// <param name="search">Optional text search filter (Japanese, romaji, or English).</param>
    /// <param name="limit">Page size, clamped to 1-200.</param>
    /// <returns>Paginated deck vocabulary list.</returns>
    [HttpGet("{id}/vocabulary")]
    [SwaggerOperation(Summary = "Get deck vocabulary")]
    [ProducesResponseType(typeof(PaginatedResponse<DeckVocabularyListDto?>), StatusCodes.Status200OK)]
    public async Task<PaginatedResponse<DeckVocabularyListDto?>> GetVocabulary(int id, string? sortBy = "",
                                                                               SortOrder sortOrder = SortOrder.Ascending,
                                                                               int? offset = 0, string displayFilter = "all",
                                                                               string? suspended = null, string? redundant = null,
                                                                               string? search = null,
                                                                               string? pos = null, string? excludePos = null,
                                                                               bool hideKanaOnly = false,
                                                                               int limit = 100,
                                                                               MediaType? frequencySource = null)
    {
        int pageSize = Math.Clamp(limit, 1, 200);

        frequencySource ??= (await frequencySourceResolver.Resolve()).MediaType;

        var deck = await context.Decks.AsNoTracking().FirstOrDefaultAsync(d => d.DeckId == id);

        if (deck == null)
            return new PaginatedResponse<DeckVocabularyListDto?>(null, 0, pageSize, offset ?? 0);

        var parentDeck = await context.Decks.AsNoTracking().FirstOrDefaultAsync(d => d.DeckId == deck.ParentDeckId);
        var parentDeckDto = parentDeck != null ? new DeckDto(parentDeck) : null;

        var query = context.DeckWords.AsNoTracking().Where(dw => dw.DeckId == id);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var matchingWordIds = await SearchHelper.ResolveSearchWordIds(context, search);
            query = query.Where(dw => matchingWordIds.Contains(dw.WordId));
        }

        var posTags = VocabularyFilterHelper.ParseCommaSeparatedTags(pos);
        if (posTags.Length > 0)
        {
            var wordIdsWithPos = context.JMDictWords.AsNoTracking()
                .Where(w => w.PartsOfSpeech.Any(p => posTags.Contains(p)));
            query = query.Where(dw => wordIdsWithPos.Any(w => w.WordId == dw.WordId));
        }

        var excludePosTags = VocabularyFilterHelper.ParseCommaSeparatedTags(excludePos);
        if (excludePosTags.Length > 0)
        {
            var wordIdsToExclude = context.JMDictWords.AsNoTracking()
                .Where(w => w.PartsOfSpeech.Any(p => excludePosTags.Contains(p)));
            query = query.Where(dw => !wordIdsToExclude.Any(w => w.WordId == dw.WordId));
        }

        if (hideKanaOnly)
        {
            query = query.Where(dw => context.WordForms
                .Any(wf => wf.WordId == dw.WordId && wf.ReadingIndex == (short)dw.ReadingIndex
                           && wf.FormType != JmDictFormType.KanaForm));
        }

        var displayFilterSpec = VocabularyDisplayFilter.Parse(displayFilter, suspended, redundant);

        if (currentUserService.IsAuthenticated && displayFilterSpec.IsActive)
        {
            var allDeckWords = await query.ToListAsync();
            var deckWordKeys = allDeckWords.Select(dw => (dw.WordId, dw.ReadingIndex)).ToList();

            var knownStates = await currentUserService.GetKnownWordsState(deckWordKeys);

            query = allDeckWords.Where(dw =>
                       displayFilterSpec.Matches(knownStates.GetValueOrDefault((dw.WordId, dw.ReadingIndex), [KnownState.New])))
                                .AsQueryable();
        }

        query = sortBy switch
        {
            "globalFreq" => frequencySource.HasValue
                ? OrderByTypeFrequency(query, frequencySource.Value, sortOrder)
                : sortOrder == SortOrder.Ascending
                    ? query.OrderBy(d => context.WordFormFrequencies
                                                .Where(wff => wff.WordId == d.WordId && wff.ReadingIndex == (short)d.ReadingIndex)
                                                .Select(wff => wff.FrequencyRank)
                                                .FirstOrDefault()).ThenBy(d => d.DeckWordId)
                    : query.OrderByDescending(d => context.WordFormFrequencies
                                                          .Where(wff => wff.WordId == d.WordId && wff.ReadingIndex == (short)d.ReadingIndex)
                                                          .Select(wff => wff.FrequencyRank)
                                                          .FirstOrDefault()).ThenBy(d => d.DeckWordId),
            "deckFreq" => sortOrder == SortOrder.Ascending
                ? query.OrderByDescending(d => d.Occurrences).ThenBy(d => d.DeckWordId)
                : query.OrderBy(d => d.Occurrences).ThenBy(d => d.DeckWordId),
            "chrono" or _ => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.DeckWordId)
                : query.OrderByDescending(d => d.DeckWordId),
        };

        int totalCount = query.Count(dw => dw.DeckId == id);

        var deckWordsList = query.Skip(offset ?? 0)
                                 .Take(pageSize)
                                 .ToList();

        var wordIds = deckWordsList.Select(dw => dw.WordId).ToList();
        var uniqueWordIds = wordIds.Distinct().ToList();

        var jmdictWordsDict = context.JMDictWords.AsNoTracking()
                                     .Where(w => uniqueWordIds.Contains(w.WordId))
                                     .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
                                     .ToDictionary(w => w.WordId);

        var wordIdOrder = new Dictionary<int, int>(capacity: wordIds.Count);
        for (int i = 0; i < wordIds.Count; i++)
        {
            wordIdOrder.TryAdd(wordIds[i], i);
        }

        var words = deckWordsList.Select(dw => new { dw, jmDictWord = jmdictWordsDict.GetValueOrDefault(dw.WordId) })
                                 .OrderBy(dw => wordIdOrder.GetValueOrDefault(dw.dw.WordId, int.MaxValue))
                                 .ToList();

        var forms = await WordFormHelper.LoadWordForms(context, uniqueWordIds);
        var scopedFreqs = await frequencySourceResolver.LoadFrequencies(context, uniqueWordIds, new FrequencyScope(frequencySource, null));

        DeckVocabularyListDto dto = new() { ParentDeck = parentDeckDto, Deck = deck, Words = new(), AppliedFrequencySource = frequencySource };

        var knownWords = await currentUserService.GetKnownWordsState(words.Select(dw => (dw.dw.WordId, dw.dw.ReadingIndex)).ToList());

        foreach (var word in words)
        {
            if (word.jmDictWord == null)
            {
                continue;
            }

            var key = (word.dw.WordId, (short)word.dw.ReadingIndex);
            var mainForm = forms.GetValueOrDefault(key);
            if (mainForm == null) continue;

            var allFormsForWord = forms.Where(f => f.Key.Item1 == word.dw.WordId)
                                       .OrderBy(f => f.Key.Item2)
                                       .Select(f => f.Value)
                                       .ToList();

            List<WordFormDto> alternativeReadings = allFormsForWord
                                                    .Where(f => f.ReadingIndex != word.dw.ReadingIndex)
                                                    .Select(f =>
                                                                WordFormHelper.ToPlainFormDto(f, scopedFreqs.Resolve(f.WordId, f.ReadingIndex)))
                                                    .ToList();

            var mainReading = WordFormHelper.ToFormDto(mainForm, scopedFreqs.Resolve(key.Item1, key.Item2));

            var wordDto = new WordDto
                          {
                              WordId = word.jmDictWord.WordId, MainReading = mainReading, AlternativeReadings = alternativeReadings,
                              PartsOfSpeech = word.jmDictWord.PartsOfSpeech.ToHumanReadablePartsOfSpeech(),
                              Definitions = word.jmDictWord.Definitions.ToDefinitionDtos(), Occurrences = word.dw.Occurrences,
                              PitchAccents = word.jmDictWord.PitchAccents
                          };

            dto.Words.Add(wordDto);
        }

        dto.Words.ApplyKnownWordsState(knownWords);

        return new PaginatedResponse<DeckVocabularyListDto?>(dto, totalCount, pageSize, offset ?? 0);
    }

    /// <summary>Words unobserved in the media type sort last in both directions rather than as rank zero.</summary>
    private IQueryable<DeckWord> OrderByTypeFrequency(IQueryable<DeckWord> query, MediaType source, SortOrder sortOrder)
    {
        var ranked = query.Select(d => new
        {
            DeckWord = d,
            Rank = context.WordFormFrequenciesByType
                          .Where(wff => wff.MediaType == source && wff.WordId == d.WordId && wff.ReadingIndex == (short)d.ReadingIndex)
                          .Select(wff => (int?)wff.FrequencyRank)
                          .FirstOrDefault() ?? int.MaxValue
        });

        ranked = sortOrder == SortOrder.Ascending
            ? ranked.OrderBy(r => r.Rank).ThenBy(r => r.DeckWord.DeckWordId)
            : ranked.OrderByDescending(r => r.Rank).ThenBy(r => r.DeckWord.DeckWordId);

        return ranked.Select(r => r.DeckWord);
    }

    private static readonly string[] FunctionWordPosTags = ["prt", "aux", "aux-v", "aux-adj", "cop", "conj", "int"];
    private const int PreviewRareRankFloor = 30_000;
    private static readonly HashSet<string> NamePosTags =
    [
        "company", "given", "place", "person", "product", "ship", "surname", "unclass", "name-fem", "name-masc", "station",
        "group", "char", "creat", "dei", "doc", "ev", "fem", "fict", "leg", "masc", "myth", "obj",
        "organization", "oth", "relig", "serv", "work", "unc"
    ];

    /// <summary>
    /// Returns notable content words of a deck (most frequent plus rare long-tail), for the SSR preview on the detail page.
    /// </summary>
    [HttpGet("{id}/vocabulary-preview")]
    [ResponseCache(Duration = 60 * 60, VaryByQueryKeys = ["limit"])]
    [SwaggerOperation(Summary = "Get notable content words of a deck (slim rows)")]
    [ProducesResponseType(typeof(List<DeckVocabularyPreviewWordDto>), StatusCodes.Status200OK)]
    public async Task<List<DeckVocabularyPreviewWordDto>> GetVocabularyPreview(int id, int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 50);
        int rareTarget = limit * 2 / 5;

        // Kanji-form requirement keeps kana filler (これ, こと, いい) out of the preview.
        var baseQuery = context.DeckWords.AsNoTracking()
                               .Where(dw => dw.DeckId == id)
                               .Where(dw => !context.JMDictWords.Any(w => w.WordId == dw.WordId &&
                                                                          w.PartsOfSpeech.Any(p => FunctionWordPosTags.Contains(p))))
                               .Where(dw => context.WordForms.Any(wf => wf.WordId == dw.WordId &&
                                                                        wf.ReadingIndex == (short)dw.ReadingIndex &&
                                                                        wf.FormType != JmDictFormType.KanaForm));

        var candidates = await baseQuery
                               .OrderByDescending(dw => dw.Occurrences).ThenBy(dw => dw.DeckWordId)
                               .Take(limit)
                               .Select(dw => new { dw.DeckWordId, dw.WordId, dw.ReadingIndex, dw.Occurrences })
                               .ToListAsync();

        var frequent = candidates.Take(limit - rareTarget).ToList();
        var takenIds = frequent.Select(f => f.DeckWordId).ToList();

        // Occurrences >= 3 keeps one-off noise out of the long-tail slots.
        var rareCandidates = await baseQuery
                                   .Where(dw => !takenIds.Contains(dw.DeckWordId) && dw.Occurrences >= 3)
                                   .Where(dw => !context.WordFormFrequencies.Any(f => f.WordId == dw.WordId &&
                                                                                      f.ReadingIndex == (short)dw.ReadingIndex &&
                                                                                      f.FrequencyRank > 0 &&
                                                                                      f.FrequencyRank <= PreviewRareRankFloor))
                                   .OrderByDescending(dw => dw.Occurrences).ThenBy(dw => dw.DeckWordId)
                                   .Take(rareTarget * 3)
                                   .Select(dw => new { dw.DeckWordId, dw.WordId, dw.ReadingIndex, dw.Occurrences })
                                   .ToListAsync();

        // Character names recur heavily and would fill every long-tail slot; cap them at half.
        var candidateWordIds = rareCandidates.Select(c => c.WordId).Distinct().ToList();
        var nameWordIds = (await context.JMDictWords.AsNoTracking()
                                        .Where(w => candidateWordIds.Contains(w.WordId))
                                        .Select(w => new { w.WordId, w.PartsOfSpeech })
                                        .ToListAsync())
                          .Where(w => w.PartsOfSpeech.Count > 0 && w.PartsOfSpeech.All(p => NamePosTags.Contains(p)))
                          .Select(w => w.WordId)
                          .ToHashSet();

        int nameCap = rareTarget / 2;
        var selectedRareIds = new HashSet<long>();
        int namesTaken = 0;
        foreach (var c in rareCandidates)
        {
            if (selectedRareIds.Count >= rareTarget) break;
            bool isName = nameWordIds.Contains(c.WordId);
            if (isName && namesTaken >= nameCap) continue;
            if (isName) namesTaken++;
            selectedRareIds.Add(c.DeckWordId);
        }

        var rare = rareCandidates.Where(c => selectedRareIds.Contains(c.DeckWordId)).ToList();

        var rareIds = rare.Select(r => r.DeckWordId).ToHashSet();
        var top = frequent.Concat(rare)
                          .Concat(candidates.Skip(limit - rareTarget).Where(c => !rareIds.Contains(c.DeckWordId)))
                          .Take(limit)
                          .ToList();

        var wordIds = top.Select(t => t.WordId).Distinct().ToList();
        if (wordIds.Count == 0)
            return [];

        var words = await context.JMDictWords.AsNoTracking()
                                 .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
                                 .Where(w => wordIds.Contains(w.WordId))
                                 .ToDictionaryAsync(w => w.WordId);
        var forms = await WordFormHelper.LoadWordForms(context, wordIds);
        var freqs = await WordFormHelper.LoadWordFormFrequencies(context, wordIds);

        return top.Select(t => new
                  {
                      t,
                      Form = forms.GetValueOrDefault((t.WordId, (short)t.ReadingIndex)),
                      Freq = freqs.GetValueOrDefault((t.WordId, (short)t.ReadingIndex)),
                  })
                  .Where(x => x.Form != null)
                  .Select(x => new DeckVocabularyPreviewWordDto
                  {
                      WordId = x.t.WordId,
                      ReadingIndex = (byte)x.t.ReadingIndex,
                      Reading = x.Form!.Text,
                      ReadingFurigana = x.Form.RubyText,
                      MainDefinition = words.GetValueOrDefault(x.t.WordId)?.Definitions.FirstOrDefault()?.EnglishMeanings.FirstOrDefault(),
                      FrequencyRank = x.Freq?.FrequencyRank,
                      Occurrences = x.t.Occurrences,
                  })
                  .ToList();
    }

    /// <summary>
    /// Returns details for a media deck including parent and subdecks.
    /// </summary>
    /// <param name="id">Deck identifier.</param>
    /// <param name="offset">Pagination offset for subdecks.</param>
    /// <param name="subdeckFilter">Case-insensitive substring matched against the subdeck titles.</param>
    /// <param name="subdeckSort">Subdeck ordering.</param>
    /// <param name="subdeckSortOrder">Direction for <paramref name="subdeckSort"/>.</param>
    /// <returns>Deck detail with subdecks.</returns>
    [HttpGet("{id}/detail")]
    [SwaggerOperation(Summary = "Get deck details")]
    [ProducesResponseType(typeof(PaginatedResponse<DeckDetailDto?>), StatusCodes.Status200OK)]
    public async Task<PaginatedResponse<DeckDetailDto?>> GetMediaDeckDetail(int id, int? offset = 0,
                                                                           string? subdeckFilter = null,
                                                                           SubdeckSort subdeckSort = SubdeckSort.Order,
                                                                           SortOrder subdeckSortOrder = SortOrder.Ascending)
    {
        int pageSize = 25;

        var deck = await context.Decks.AsNoTracking()
                                .Include(d => d.Children)
                                .Include(d => d.Links)
                                .Include(d => d.DeckGenres)
                                .Include(d => d.DeckTags)
                                .ThenInclude(dt => dt.Tag)
                                .Include(d => d.DeckDifficulty)
                                .Include(d => d.RelationshipsAsSource)
                                .ThenInclude(r => r.TargetDeck)
                                .Include(d => d.RelationshipsAsTarget)
                                .ThenInclude(r => r.SourceDeck)
                                .FirstOrDefaultAsync(d => d.DeckId == id);

        if (deck == null)
            return new PaginatedResponse<DeckDetailDto?>(null, 0, pageSize, offset ?? 0);

        var parentDeck = await context.Decks.AsNoTracking().Include(d => d.DeckGenres).Include(d => d.DeckTags).ThenInclude(dt => dt.Tag).Include(d => d.DeckDifficulty)
                                      .FirstOrDefaultAsync(d => d.DeckId == deck.ParentDeckId);
        var subDecks = context.Decks.AsNoTracking().Include(d => d.DeckGenres).Include(d => d.DeckTags).ThenInclude(dt => dt.Tag).Include(d => d.DeckDifficulty)
                              .Where(d => d.ParentDeckId == id);

        if (!string.IsNullOrWhiteSpace(subdeckFilter))
        {
            // lower() + LIKE rather than ILIKE: the integration suite runs on SQLite, which has no ILIKE.
            var pattern = $"%{EscapeLikeWildcards(subdeckFilter.Trim().ToLower())}%";
            subDecks = subDecks.Where(d => EF.Functions.Like(d.OriginalTitle.ToLower(), pattern, LikeEscapeCharacter)
                                           || (d.RomajiTitle != null && EF.Functions.Like(d.RomajiTitle.ToLower(), pattern, LikeEscapeCharacter))
                                           || (d.EnglishTitle != null && EF.Functions.Like(d.EnglishTitle.ToLower(), pattern, LikeEscapeCharacter)));
        }

        int totalCount = await subDecks.CountAsync();

        subDecks = (subdeckSort, subdeckSortOrder) switch
        {
            (SubdeckSort.Difficulty, SortOrder.Descending) => subDecks.OrderByDescending(SubdeckDifficultyKey).ThenBy(d => d.DeckOrder),
            (SubdeckSort.Difficulty, _) => subDecks.OrderBy(SubdeckDifficultyKey).ThenBy(d => d.DeckOrder),
            (_, SortOrder.Descending) => subDecks.OrderByDescending(d => d.DeckOrder),
            _ => subDecks.OrderBy(d => d.DeckOrder),
        };

        subDecks = subDecks
                   .Skip(offset ?? 0)
                   .Take(pageSize);

        var mainDeckDto = new DeckDto(deck);
        mainDeckDto.Relationships = DeckRelationshipDto.FromDeck(deck.RelationshipsAsSource, deck.RelationshipsAsTarget);
        List<DeckDto> subdeckDtos = [];

        var subDeckList = await subDecks.ToListAsync();
        foreach (var subDeck in subDeckList)
            subdeckDtos.Add(new DeckDto(subDeck));

        if (currentUserService.IsAuthenticated)
        {
            var userId = currentUserService.UserId!;
            var ids = new List<int> { mainDeckDto.DeckId };
            ids.AddRange(subdeckDtos.Select(d => d.DeckId));

            // On-demand child coverage: child slots are 0 after a parent-only daily recompute.
            // Compute the visible page inline immediately; enqueue a background job for all siblings.
            var childIds = deck.ParentDeckId.HasValue
                ? [deck.DeckId]                                      // visiting a child deck directly
                : subdeckDtos.Select(d => d.DeckId).ToArray();      // visiting a parent deck

            if (childIds.Length > 0 && await userContext.UserCoverageChunks.AnyAsync(c => c.UserId == userId))
            {
                var existingCoverage = await UserCoverageChunkHelper.GetCoverage(userContext, userId, childIds);
                if (childIds.Any(cid => existingCoverage.MatureCoverage.GetValueOrDefault(cid) == 0f))
                {
                    var parentId = deck.ParentDeckId ?? deck.DeckId;
                    await CoverageComputeService.ComputeSpecificDecksAsync(userContext, userId, childIds);
                    backgroundJobClient.Enqueue<ComputationJob>(job => job.ComputeUserChildrenCoverage(userId, parentId));
                }
            }

            var coverages = await UserCoverageChunkHelper.GetCoverage(userContext, userId, ids);
            var coverageDict = coverages.MatureCoverage;
            var uCoverageDict = coverages.MatureUniqueCoverage;
            var yCoverageDict = coverages.YoungCoverage;
            var yUCoverageDict = coverages.YoungUniqueCoverage;

            var preferences = await userContext.UserDeckPreferences.AsNoTracking()
                                               .Where(p => p.UserId == userId && ids.Contains(p.DeckId))
                                               .ToListAsync();
            var preferencesDict = preferences.ToDictionary(p => p.DeckId);

            if (coverageDict.TryGetValue(mainDeckDto.DeckId, out var mc)) mainDeckDto.Coverage = mc;
            if (uCoverageDict.TryGetValue(mainDeckDto.DeckId, out var muc)) mainDeckDto.UniqueCoverage = muc;
            if (yCoverageDict.TryGetValue(mainDeckDto.DeckId, out var myc)) mainDeckDto.YoungCoverage = myc;
            if (yUCoverageDict.TryGetValue(mainDeckDto.DeckId, out var myuc)) mainDeckDto.YoungUniqueCoverage = myuc;
            if (preferencesDict.TryGetValue(mainDeckDto.DeckId, out var mpref))
            {
                mainDeckDto.Status = mpref.Status;
                mainDeckDto.IsFavourite = mpref.IsFavourite;
                mainDeckDto.IsIgnored = mpref.IsIgnored;
            }

            foreach (var subdeckDto in subdeckDtos)
            {
                if (coverageDict.TryGetValue(subdeckDto.DeckId, out var c)) subdeckDto.Coverage = c;
                if (uCoverageDict.TryGetValue(subdeckDto.DeckId, out var uc)) subdeckDto.UniqueCoverage = uc;
                if (yCoverageDict.TryGetValue(subdeckDto.DeckId, out var yc)) subdeckDto.YoungCoverage = yc;
                if (yUCoverageDict.TryGetValue(subdeckDto.DeckId, out var yuc)) subdeckDto.YoungUniqueCoverage = yuc;
                if (preferencesDict.TryGetValue(subdeckDto.DeckId, out var pref))
                {
                    subdeckDto.Status = pref.Status;
                    subdeckDto.IsFavourite = pref.IsFavourite;
                    subdeckDto.IsIgnored = pref.IsIgnored;
                }
            }
        }

        var parentDeckDto = parentDeck != null ? new DeckDto(parentDeck) : null;
        var dto = new DeckDetailDto { ParentDeck = parentDeckDto, MainDeck = mainDeckDto, SubDecks = subdeckDtos };

        return new PaginatedResponse<DeckDetailDto?>(dto, totalCount, pageSize, offset ?? 0);
    }

    /// <summary>Widens a deck id to its whole media: the root plus every child, whichever member was passed.</summary>
    internal static async Task<List<int>?> ResolveMediaDeckIdsAsync(JitenDbContext context, int deckId)
    {
        var deck = await context.Decks.AsNoTracking()
                                .Where(d => d.DeckId == deckId)
                                .Select(d => new { d.DeckId, d.ParentDeckId })
                                .FirstOrDefaultAsync();
        if (deck == null)
            return null;

        var rootId = deck.ParentDeckId ?? deck.DeckId;
        var ids = new List<int> { rootId };
        ids.AddRange(await context.Decks.AsNoTracking()
                                  .Where(d => d.ParentDeckId == rootId)
                                  .Select(d => d.DeckId)
                                  .ToListAsync());
        return ids;
    }

    /// <summary>
    /// Synchronously recomputes the viewer's coverage for a whole media (root deck plus all subdecks).
    /// </summary>
    /// <param name="id">Any deck of the media: the root or one of its subdecks.</param>
    [HttpPost("{id}/coverage/refresh")]
    [Authorize]
    [EnableRateLimiting("coverage-refresh")]
    [SwaggerOperation(Summary = "Refresh the viewer's coverage for a media and its subdecks")]
    [ProducesResponseType(typeof(DeckCoverageRefreshResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IResult> RefreshDeckCoverage(int id)
    {
        var userId = currentUserService.UserId!;

        var deckIds = await ResolveMediaDeckIdsAsync(context, id);
        if (deckIds == null)
            return Results.NotFound();

        // Same eligibility gate as ComputationJob: a recompute for a user with almost no tracked words only writes zeroes.
        var hasSufficientFsrsCards = await userContext.FsrsCards.CountAsync(fc => fc.UserId == userId) >= 10;
        var hasWordSetSubscriptions = await userContext.UserWordSetStates.AnyAsync(uwss => uwss.UserId == userId);
        if (!hasSufficientFsrsCards && !hasWordSetSubscriptions)
            return Results.Ok(new DeckCoverageRefreshResponse { Status = "not_eligible" });

        // Without a full baseline, filling individual slots would make every other deck read 0; the client offers the account-wide refresh instead.
        if (!await userContext.UserCoverageChunks.AnyAsync(c => c.UserId == userId))
            return Results.Ok(new DeckCoverageRefreshResponse { Status = "no_baseline" });

        var previousTimeout = userContext.Database.GetCommandTimeout();
        userContext.Database.SetCommandTimeout(TimeSpan.FromSeconds(120));
        try
        {
            await CoverageComputeService.ComputeSpecificDecksAsync(userContext, userId, deckIds);
        }
        finally
        {
            userContext.Database.SetCommandTimeout(previousTimeout);
        }

        var coverage = await UserCoverageChunkHelper.GetCoverage(userContext, userId, [id]);
        return Results.Ok(new DeckCoverageRefreshResponse
        {
            Status = "refreshed",
            Coverage = coverage.MatureCoverage.GetValueOrDefault(id),
            UniqueCoverage = coverage.MatureUniqueCoverage.GetValueOrDefault(id),
            YoungCoverage = coverage.YoungCoverage.GetValueOrDefault(id),
            YoungUniqueCoverage = coverage.YoungUniqueCoverage.GetValueOrDefault(id),
        });
    }

    /// <summary>
    /// Downloads a deck in the requested format and order. Supports filtering and excluding known words.
    /// </summary>
    /// <param name="id">Deck identifier.</param>
    /// <param name="request">Download options.</param>
    /// <returns>File content result with the generated deck.</returns>
    [HttpPost("{id}/download")]
    [EnableRateLimiting("download")]
    [SwaggerOperation(Summary = "Download a deck",
                      Description = "Generate a deck file (Anki, CSV, TXT, Yomitan) with optional filters and ordering.")]
    [Produces("application/x-binary", "text/csv", "text/plain", "application/zip")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IResult> DownloadDeck(int id, [FromBody] DeckDownloadRequest request)
    {
        var deck = await context.Decks
                                .AsNoTracking()
                                .Include(d => d.Children)
                                .FirstOrDefaultAsync(d => d.DeckId == id);

        if (deck == null)
        {
            return Results.NotFound();
        }

        if (request.Format == DeckFormat.Yomitan)
        {
            var yomitanBytes = await YomitanHelper.GenerateYomitanFrequencyDeckFromDeck(contextFactory, deck);
            return Results.File(yomitanBytes, "application/zip", $"freq_{deck.OriginalTitle}.zip");
        }

        var (deckWordsRaw, error) = await ResolveDeckWords(
                                                           id, deck, request.DownloadType, request.Order,
                                                           request.MinFrequency, request.MaxFrequency,
                                                           request.ExcludeMatureMasteredBlacklisted, request.ExcludeAllTrackedWords,
                                                           request.TargetPercentage,
                                                           request.MinOccurrences, request.MaxOccurrences,
                                                           request.StartFromKnown, request.FrequencySource);

        if (error != null)
            return error;

        var wordIds = deckWordsRaw!.Select(dw => (long)dw.WordId).ToList();

        List<(int WordId, byte ReadingIndex, int Occurrences)> deckWords = deckWordsRaw!
                                                                           .Select(dw => new ValueTuple<int, byte, int>(dw.WordId,
                                                                                       dw.ReadingIndex, dw.Occurrences))
                                                                           .ToList();

        var sentenceDeckIds = deck.Children.Count != 0
            ? deck.Children.Select(c => c.DeckId).ToList()
            : new List<int> { id };
        var bytes = await downloadService.GenerateDownload(request, wordIds, deck.OriginalTitle, deckWords, sentenceDeckIds);

        if (bytes == null)
            return Results.BadRequest();

        await RecordDownloadAsync(id);

        logger.LogInformation(
                              "User downloaded deck: DeckId={DeckId}, DeckTitle={DeckTitle}, Format={Format}, DownloadType={DownloadType}, WordCount={WordCount}, ExcludeMature={ExcludeMature}, ExcludeAllTracked={ExcludeAllTracked}",
                              id, deck.OriginalTitle, request.Format, request.DownloadType, deckWordsRaw!.Count,
                              request.ExcludeMatureMasteredBlacklisted, request.ExcludeAllTrackedWords);

        return request.Format switch
        {
            DeckFormat.Anki => Results.File(bytes, "application/x-binary", $"{deck.OriginalTitle}.apkg"),
            DeckFormat.Csv => Results.File(bytes, "text/csv", $"{deck.OriginalTitle}.csv"),
            DeckFormat.Txt or DeckFormat.TxtRepeated => Results.File(bytes, "text/plain", $"{deck.OriginalTitle}.txt"),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    [HttpPost("{id:int}/view")]
    [AllowAnonymous]
    [EnableRateLimiting("fixed")]
    [SwaggerOperation(Summary = "Record an engaged view of a deck",
                      Description = "Fired by the deck page after a delay.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IResult RecordView(int id)
    {
        if (id > 0)
            activityBuffer.RecordView(id, VisitorKey(HttpContext));
        return Results.NoContent();
    }

    /// <summary>The proxy-resolved connection address is the only value a client cannot forge; headers are a fallback for calls arriving from inside the network.</summary>
    private static string VisitorKey(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote != null && !IsInternal(remote)) return remote.ToString();

        foreach (var header in new[] { "CF-Connecting-IP", "X-Real-IP", "X-Forwarded-For" })
        {
            var value = context.Request.Headers[header].FirstOrDefault();
            if (string.IsNullOrEmpty(value)) continue;
            var ip = value.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(ip) && ip != "unknown") return ip;
        }

        return remote?.ToString() ?? "unknown";
    }

    private static bool IsInternal(System.Net.IPAddress ip)
    {
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private async Task RecordDownloadAsync(int deckId)
    {
        var userId = currentUserService.UserId;
        if (userId == null)
        {
            activityBuffer.RecordGuestDownload(deckId);
            return;
        }

        var exists = await userContext.DeckDownloads.AnyAsync(d => d.UserId == userId && d.DeckId == deckId);
        if (exists) return;

        userContext.DeckDownloads.Add(new DeckDownload { UserId = userId, DeckId = deckId, FirstDownloadAt = DateTime.UtcNow });
        try
        {
            await userContext.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            userContext.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Marks the resolved vocabulary of a deck as mastered or blacklisted in the user's vocabulary tracker.
    /// </summary>
    /// <param name="id">Deck identifier.</param>
    /// <param name="request">Learn options including vocabulary state.</param>
    /// <returns>Count of applied words and the state.</returns>
    [HttpPost("{id}/learn")]
    [Authorize]
    [EnableRateLimiting("download")]
    [SwaggerOperation(Summary = "Bulk-apply vocabulary from a deck",
                      Description = "Mark resolved vocabulary as mastered or blacklisted. No file is generated.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IResult> LearnDeck(int id, [FromBody] DeckLearnRequest request)
    {
        var state = request.VocabularyState?.ToLowerInvariant();
        if (state is not ("mastered" or "blacklisted"))
            return Results.BadRequest("VocabularyState must be 'mastered' or 'blacklisted'.");

        var deck = await context.Decks
                                .AsNoTracking()
                                .Include(d => d.Children)
                                .FirstOrDefaultAsync(d => d.DeckId == id);

        if (deck == null)
            return Results.NotFound();

        var (deckWordsRaw, error) = await ResolveDeckWords(
                                                           id, deck, request.DownloadType, request.Order,
                                                           request.MinFrequency, request.MaxFrequency,
                                                           request.ExcludeMatureMasteredBlacklisted, request.ExcludeAllTrackedWords,
                                                           request.TargetPercentage,
                                                           request.MinOccurrences, request.MaxOccurrences,
                                                           request.StartFromKnown, request.FrequencySource);

        if (error != null)
            return error;

        if (request.ExcludeKana)
        {
            var wordIds = deckWordsRaw!.Select(dw => dw.WordId).Distinct().ToList();
            var excludeKanaForms = await WordFormHelper.LoadWordForms(context, wordIds);

            deckWordsRaw = deckWordsRaw!.Where(dw =>
            {
                var form = excludeKanaForms.GetValueOrDefault((dw.WordId, (short)dw.ReadingIndex));
                if (form == null) return true;
                return !WanaKana.IsKana(form.Text);
            }).ToList();
        }

        var applied = (state == "mastered"
            ? await currentUserService.AddKnownWords(deckWordsRaw!, countAsNewlyLearned: request.CountAsNewlyLearned)
            : await currentUserService.BlacklistWords(deckWordsRaw!)).Inserted;

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, currentUserService.UserId!);
        await userContext.SaveChangesAsync();

        logger.LogInformation(
                              "User applied learn to deck: DeckId={DeckId}, DeckTitle={DeckTitle}, State={State}, WordCount={WordCount}",
                              id, deck.OriginalTitle, state, applied);

        return Results.Ok(new { applied, state });
    }

    /// <summary>
    /// Parses a custom text into a temporary deck and returns the generated Anki package as base64.
    /// </summary>
    /// <param name="request">Text to parse.</param>
    /// <returns>JSON containing deck metadata and a base64-encoded file.</returns>
    [HttpPost("parse-custom-deck")]
    [EnableRateLimiting("download")]
    [RequestSizeLimit(5_000_000)]
    [SwaggerOperation(Summary = "Parse custom deck text")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IResult> ParseCustomDeck([FromBody] ParseCustomDeckRequest request)
    {
        if (request.Text.Length > 200000)
            return Results.BadRequest();

        var deck = await Parser.Parser.ParseTextToDeck(contextFactory, storeRawText: true, text: request.Text);
        deck.OriginalTitle = "Custom deck";
        var deckDownloadRequest = new DeckDownloadRequest() { DownloadType = DeckDownloadType.Full, Format = DeckFormat.Anki };
        var deckWords = deck.DeckWords.Select(dw => new ValueTuple<int, byte, int>(dw.WordId, dw.ReadingIndex, dw.Occurrences)).ToList();
        var wordIds = deck.DeckWords.Select(dw => (long)dw.WordId).ToList();

        var fileResult = await downloadService.GenerateDownload(deckDownloadRequest, wordIds, deck.OriginalTitle, deckWords, null);
        var deckDto = new DeckDto(deck);
        var fileBase64 = Convert.ToBase64String(fileResult!);

        logger.LogInformation(
                              "User parsed custom deck: CharacterCount={CharacterCount}, WordCount={WordCount}, UniqueWordCount={UniqueWordCount}",
                              deck.CharacterCount, deck.WordCount, deck.UniqueWordCount);

        var result = new
                     {
                         Deck = deckDto, File = new
                                                {
                                                    ContentBase64 = fileBase64, ContentType = "application/x-binary", // Mime type for .apkg
                                                    FileName = $"{deck.OriginalTitle}.apkg"
                                                }
                     };
        return Results.Json(result);
    }

    /// <summary>
    /// Returns the count of top-level decks per media type.
    /// </summary>
    [HttpGet("decks-count")]
    [ResponseCache(Duration = 600)]
    [SwaggerOperation(Summary = "Get deck counts by media type")]
    [ProducesResponseType(typeof(Dictionary<int, int>), StatusCodes.Status200OK)]
    public IResult GetDecksCountByMediaType()
    {
        Dictionary<int, int> decksCount = context.Decks.AsNoTracking()
                                                 .Where(d => d.ParentDeckId == null)
                                                 .GroupBy(d => d.MediaType)
                                                 .ToDictionary(g => (int)g.Key, g => g.Count());

        return Results.Ok(decksCount);
    }

    /// <summary>
    /// Returns the number of vocabulary items in a deck between global frequency ranks.
    /// </summary>
    /// <param name="id">Deck identifier.</param>
    /// <param name="minFrequency">Minimum global frequency rank (inclusive).</param>
    /// <param name="maxFrequency">Maximum global frequency rank (inclusive).</param>
    /// <param name="frequencySource">Media type whose ranking to use; omitted means the site-wide ranking.</param>
    [HttpGet("{id}/vocabulary-count-frequency")]
    [SwaggerOperation(Summary = "Count vocabulary in frequency range")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    public IResult GetVocabularyCountByMediaFrequencyRange(int id, int minFrequency, int maxFrequency,
                                                           MediaType? frequencySource = null)
    {
        var deckWords = context.DeckWords.AsNoTracking().Where(dw => dw.DeckId == id);

        if (frequencySource.HasValue)
        {
            var source = frequencySource.Value;
            return Results.Ok(deckWords.Count(dw => context.WordFormFrequenciesByType
                                                           .Any(wff => wff.MediaType == source &&
                                                                       wff.WordId == dw.WordId &&
                                                                       wff.ReadingIndex == (short)dw.ReadingIndex &&
                                                                       wff.FrequencyRank >= minFrequency &&
                                                                       wff.FrequencyRank <= maxFrequency)));
        }

        var count = deckWords.Count(dw => context.WordFormFrequencies
                                                 .Any(wff => wff.WordId == dw.WordId &&
                                                             wff.ReadingIndex == (short)dw.ReadingIndex &&
                                                             wff.FrequencyRank >= minFrequency &&
                                                             wff.FrequencyRank <= maxFrequency));

        return Results.Ok(count);
    }

    /// <summary>
    /// Returns the number of vocabulary items in a deck filtered by occurrence count thresholds.
    /// </summary>
    /// <param name="id">Deck identifier.</param>
    /// <param name="minOccurrences">Minimum occurrence count (inclusive, optional).</param>
    /// <param name="maxOccurrences">Maximum occurrence count (inclusive, optional).</param>
    [HttpGet("{id}/vocabulary-count-occurrences")]
    [SwaggerOperation(Summary = "Count vocabulary by occurrence count")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    public IResult GetVocabularyCountByOccurrences(int id, int? minOccurrences = null, int? maxOccurrences = null)
    {
        var query = context.DeckWords.AsNoTracking().Where(dw => dw.DeckId == id);

        if (minOccurrences.HasValue)
            query = query.Where(dw => dw.Occurrences >= minOccurrences.Value);
        if (maxOccurrences.HasValue)
            query = query.Where(dw => dw.Occurrences <= maxOccurrences.Value);

        return Results.Ok(query.Count());
    }

    /// <summary>
    /// Returns the number of vocabulary items in a deck after applying the same filters used for downloads/learn.
    /// </summary>
    /// <param name="id">Deck identifier.</param>
    /// <param name="request">Download options.</param>
    [HttpPost("{id}/vocabulary-count")]
    [SwaggerOperation(Summary = "Count vocabulary for download options")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IResult> GetVocabularyCount(int id, [FromBody] DeckDownloadRequest request)
    {
        var deck = await context.Decks.AsNoTracking().FirstOrDefaultAsync(d => d.DeckId == id);
        if (deck == null)
            return Results.NotFound();

        var (deckWordsRaw, error) = await ResolveDeckWords(
                                                           id, deck,
                                                           request.DownloadType, DeckOrder.DeckFrequency,
                                                           request.MinFrequency, request.MaxFrequency,
                                                           request.ExcludeMatureMasteredBlacklisted, request.ExcludeAllTrackedWords,
                                                           request.TargetPercentage,
                                                           request.MinOccurrences, request.MaxOccurrences,
                                                           request.StartFromKnown, request.FrequencySource);

        if (error != null)
            return error;

        if (deckWordsRaw == null || deckWordsRaw.Count == 0)
            return Results.Ok(0);

        if (request.ExcludeKana)
        {
            var wordIds = deckWordsRaw.Select(dw => dw.WordId).Distinct().ToList();
            var excludeKanaForms = await WordFormHelper.LoadWordForms(context, wordIds);

            deckWordsRaw = deckWordsRaw.Where(dw =>
            {
                var form = excludeKanaForms.GetValueOrDefault((dw.WordId, (short)dw.ReadingIndex));
                if (form == null) return true;
                return !WanaKana.IsKana(form.Text);
            }).ToList();
        }

        return Results.Ok(deckWordsRaw.Count);
    }

    /// <summary>
    /// Gets decks from sliding 30-day windows based on offset for display in the update log
    /// </summary>
    /// <param name="offset">Window offset: 0 = last 30 days, 1 = days 30-60 ago, 2 = days 60-90 ago, etc.</param>
    /// <returns>Deck information for the specified 30-day window</returns>
    [HttpGet("media-update-log")]
    [ResponseCache(Duration = 60 * 10, VaryByQueryKeys = ["offset"])]
    [SwaggerOperation(Summary = "Get decks for update log")]
    [ProducesResponseType(typeof(PaginatedResponse<List<DeckDto>>), StatusCodes.Status200OK)]
    public async Task<PaginatedResponse<List<DeckDto>>> GetDecksForUpdateLog(int? offset = 0)
    {
        int offsetValue = offset ?? 0;
        var endDate = DateTimeOffset.UtcNow.AddDays(-30 * offsetValue);
        var startDate = endDate.AddDays(-30);

        var query = context.Decks.AsNoTracking()
                           .Where(d => d.ParentDeckId == null &&
                                       d.CreationDate >= startDate &&
                                       d.CreationDate < endDate)
                           .OrderByDescending(d => d.CreationDate);

        int totalCount = await query.CountAsync();

        var decks = await query.ToListAsync();

        var dtos = decks.Select(d => new DeckDto
                                     {
                                         DeckId = d.DeckId, CreationDate = d.CreationDate, OriginalTitle = d.OriginalTitle,
                                         RomajiTitle = d.RomajiTitle!, EnglishTitle = d.EnglishTitle!, MediaType = d.MediaType
                                     }).ToList();

        return new PaginatedResponse<List<DeckDto>>(dtos, totalCount, decks.Count, offsetValue);
    }

    /// <summary>
    /// Returns deck IDs that have a link of the specified type whose trailing URL segment matches the provided id.
    /// </summary>
    /// <param name="linkType">External link type.</param>
    /// <param name="id">Trailing identifier from the link URL.</param>
    [HttpGet("by-link-id/{linkType}/{id}")]
    [ResponseCache(Duration = 600, VaryByQueryKeys = ["id"])]
    [SwaggerOperation(Summary = "Find decks by external link id")]
    [ProducesResponseType(typeof(List<int>), StatusCodes.Status200OK)]
    public async Task<List<int>> GetMediaDeckIdsByLinkId(LinkType linkType, string id)
    {
        var suffix = "/" + id.ToLowerInvariant();

        return await context.Set<Link>()
                            .Where(l => l.LinkType == linkType)
                            .Where(l => l.Url.ToLower().EndsWith(suffix) ||
                                        l.Url.ToLower().EndsWith(suffix + "/"))
                            .Select(l => l.DeckId)
                            .Distinct()
                            .ToListAsync();
    }

    /// <summary>
    /// Returns all available tags for filtering (only tags with at least one associated media).
    /// </summary>
    [HttpGet("tags")]
    [ResponseCache(Duration = 3600)]
    [SwaggerOperation(Summary = "Get all tags")]
    [ProducesResponseType(typeof(List<TagDto>), StatusCodes.Status200OK)]
    public async Task<List<TagDto>> GetAllTags()
    {
        return await context.Tags
                            .AsNoTracking()
                            .Where(t => t.DeckTags.Any())
                            .OrderBy(t => t.Name)
                            .Select(t => new TagDto { TagId = t.TagId, Name = t.Name })
                            .ToListAsync();
    }

    /// <summary>
    /// Report an issue with a deck
    /// </summary>
    /// <param name="request">Issue type and comment.</param>
    /// <returns>Did the report get sent successfully.</returns>
    [HttpPost("report")]
    [EnableRateLimiting("download")]
    [SwaggerOperation(Summary = "Report an issue with a deck")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ReportIssue([FromBody] ReportIssueRequest request)
    {
        if (!currentUserService.IsAuthenticated)
            return BadRequest("You are not logged in");

        if (string.IsNullOrEmpty(request.IssueType) || string.IsNullOrEmpty(request.Comment) || request.IssueType.Length > 30 ||
            request.Comment.Length > 1000)
            return BadRequest();

        var deck = await context.Decks.FirstOrDefaultAsync(d => d.DeckId == request.DeckId);

        if (deck == null)
            return BadRequest("Deck not found");

        var safeComment = SanitizeForDiscord(request.Comment);
        var safeIssueType = SanitizeForDiscord(request.IssueType);

        var discordPayload = new
                             {
                                 content = $"A new report from user ID `{currentUserService.UserId}` came in.\n", tts = false, embeds =
                                     new[]
                                     {
                                         new
                                         {
                                             id = 652627557, title = safeIssueType, description =
                                                 $"[{deck.OriginalTitle}](https://jiten.moe/decks/media/{deck.DeckId}/detail)\n\nComment:\n{safeComment}",
                                             color = 8266731, fields = Array.Empty<object>()
                                         }
                                     },
                                 components = Array.Empty<object>(), actions = new { }, flags = 0, username = "IssueReporter"
                             };
        var embedJson = JsonSerializer.Serialize(discordPayload);
        var webhook = configuration["DiscordWebhook"];
        using var httpClient = httpClientFactory.CreateClient();
        var content = new StringContent(embedJson, Encoding.UTF8, "application/json");
        var result = await httpClient.PostAsync(webhook, content);

        if (result.IsSuccessStatusCode)
        {
            logger.LogInformation("User reported deck issue: DeckId={DeckId}, IssueType={IssueType}",
                                  request.DeckId, request.IssueType);
            return Ok();
        }

        logger.LogWarning("Failed to send deck issue report: DeckId={DeckId}, IssueType={IssueType}",
                          request.DeckId, request.IssueType);
        return BadRequest("Failed to send report");

        string SanitizeForDiscord(string input)
        {
            if (string.IsNullOrEmpty(input))
                return "";

            var urlRegex = new Regex(@"(https?:\/\/[^\s)]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            var sb = new StringBuilder();
            int lastIndex = 0;

            foreach (Match match in urlRegex.Matches(input))
            {
                if (match.Index > lastIndex)
                {
                    var textPart = input.Substring(lastIndex, match.Index - lastIndex);
                    sb.Append(EscapeMarkdown(textPart));
                }

                // Add URL unescaped
                sb.Append(match.Value);

                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < input.Length)
            {
                sb.Append(EscapeMarkdown(input.Substring(lastIndex)));
            }

            return sb.ToString();
        }

        string EscapeMarkdown(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                // Escape markdown/meta characters but leave slashes and colons for URLs
                if (c is '*' or '_' or '~' or '`' or '>' or '|' or '[' or ']' or '(' or ')' or '@' or '#' or ':' or '"')
                {
                    sb.Append('\\');
                }

                sb.Append(c);
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Get advanced stats for a deck such as coverage
    /// </summary>
    /// <param name="id">Deck ID</param>
    /// <returns>Advanced stats</returns>
    [HttpGet("{id}/stats")]
    [ResponseCache(Duration = 3600)]
    [SwaggerOperation(Summary = "Get advanced stats for a deck",
                      Description =
                          "Returns advanced deck statistics such as parametric coverage showing how many of the most frequent words are needed for various coverage percentages")]
    [ProducesResponseType(typeof(DeckStatsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeckStatsDto>> GetCoverageStats(int id)
    {
        var deckStats = await context.DeckStats
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(ds => ds.DeckId == id);

        if (deckStats == null)
        {
            return NotFound(new { message = "Missing deck stats" });
        }

        var milestones = deckStats.GetMilestones();

        return Ok(new DeckStatsDto()
                  {
                      DeckId = id, TotalUniqueWords = deckStats.TotalUniqueWords ?? 0, ComputedAt = deckStats.ComputedAt,
                      RSquared = deckStats.RSquared ?? 0,
                      Milestones = new Dictionary<string, int>
                                   {
                                       { "80%", milestones.TryGetValue(80, out var v80) ? v80 : 0 },
                                       { "85%", milestones.TryGetValue(85, out var v85) ? v85 : 0 },
                                       { "90%", milestones.TryGetValue(90, out var v90) ? v90 : 0 },
                                       { "95%", milestones.TryGetValue(95, out var v95) ? v95 : 0 },
                                       { "98%", milestones.TryGetValue(98, out var v98) ? v98 : 0 },
                                       { "99%", milestones.TryGetValue(99, out var v99) ? v99 : 0 }
                                   }
                  });
    }

    /// <summary>
    /// Get full coverage curve data for charting
    /// </summary>
    /// <param name="id">Deck ID</param>
    /// <param name="points">Number of data points (ignored if sampled data exists)</param>
    /// <returns>List of (rank, coverage) pairs - sampled at 1% intervals (0-99%), 0.1% intervals (99-100%)</returns>
    [HttpGet("{id}/coverage-curve")]
    [ResponseCache(Duration = 3600)]
    [SwaggerOperation(Summary = "Get full coverage curve for charting",
                      Description = "Returns sampled coverage data points for interactive visualisation (~108 points)")]
    [ProducesResponseType(typeof(List<CurveDatumDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<CurveDatumDto>>> GetCoverageCurve(int id, [FromQuery] int points = 50)
    {
        var deckStats = await context.DeckStats
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(ds => ds.DeckId == id);

        if (deckStats == null)
        {
            return NotFound(new { message = "Coverage statistics not yet computed for this deck" });
        }

        // If sampled data exists, 'points' parameter is ignored
        var curvePoints = deckStats.GenerateCurvePoints(points);

        return Ok(curvePoints.Select(p => new CurveDatumDto
                                          {
                                              Rank = p.rank,
                                              // Round to whole number before 99%, keep 2 decimals at 99%+
                                              Coverage = p.coverage < 99.0 ? Math.Round(p.coverage, 0) : Math.Round(p.coverage, 2)
                                          }).ToList());
    }

    [HttpGet("{id}/coverage-journey")]
    [Authorize]
    [JitenPlus(Feature = "coverage-journey")]
    [EnableRateLimiting("journey")]
    [SwaggerOperation(Summary = "Get the user's coverage journey for a deck",
                      Description = "Coverage of this deck over time, derived from the dates the user first studied each word they know.")]
    [ProducesResponseType(typeof(JourneyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<JourneyDto>> GetCoverageJourney(int id, CancellationToken ct)
    {
        var userId = currentUserService.UserId;
        if (userId == null)
            return Unauthorized();

        var journey = await coverageJourneyService.GetDeckJourneyAsync(userId, id, ct);
        if (journey == null)
            return NotFound();

        return Ok(journey);
    }

    /// <summary>
    /// Returns detailed difficulty metrics for a deck (deciles, progression).
    /// </summary>
    [HttpGet("{id}/difficulty")]
    [ResponseCache(Duration = 3600)]
    [SwaggerOperation(Summary = "Get detailed difficulty metrics")]
    [ProducesResponseType(typeof(DeckDifficultyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeckDifficultyDto>> GetDeckDifficulty(int id)
    {
        var difficulty = await context.DeckDifficulties
                                      .AsNoTracking()
                                      .FirstOrDefaultAsync(dd => dd.DeckId == id);

        if (difficulty == null)
            return NotFound();

        return new DeckDifficultyDto
               {
                   Difficulty = difficulty.Difficulty, Peak = difficulty.Peak, Deciles = difficulty.Deciles,
                   Progression = difficulty.Progression.Select(p => new ProgressionSegmentDto
                                                                    {
                                                                        Segment = p.Segment, Difficulty = p.Difficulty, Peak = p.Peak,
                                                                        ChildStartOrder = p.ChildStartOrder, ChildEndOrder = p.ChildEndOrder
                                                                    }).ToList(),
                   LastUpdated = difficulty.LastUpdated,
                   DistinctVoterCount = difficulty.DistinctVoterCount,
                   UserAdjustment = difficulty.UserAdjustment,
                   AdjustmentConfidence = difficulty.AdjustmentConfidence
               };
    }

    private async Task<(List<DeckWord>? Words, IResult? Error)> ResolveDeckWords(
        int deckId, Deck deck,
        DeckDownloadType downloadType, DeckOrder order,
        int minFrequency, int maxFrequency,
        bool excludeMatureMasteredBlacklisted, bool excludeAllTrackedWords,
        float? targetPercentage,
        int? minOccurrences = null, int? maxOccurrences = null,
        bool startFromKnown = false, MediaType? frequencySource = null)
    {
        return await deckWordResolver.ResolveDeckWords(new DeckWordResolveRequest(
            deckId, deck, downloadType, order,
            minFrequency, maxFrequency,
            excludeMatureMasteredBlacklisted, excludeAllTrackedWords,
            targetPercentage, minOccurrences, maxOccurrences,
            StartFromKnown: startFromKnown, FrequencySource: frequencySource));
    }

    private const string LikeEscapeCharacter = "\\";

    /// <summary>
    /// Same difficulty expression the browse endpoint sorts by (override wins, community adjustment applied).
    /// Stays float: SQLite refuses decimal in ORDER BY, so a decimal key would break the integration suite.
    /// </summary>
    private static readonly Expression<Func<Deck, float>> SubdeckDifficultyKey =
        d => (d.DifficultyOverride > -1 ? d.DifficultyOverride : d.Difficulty)
             + (float)(d.DeckDifficulty != null ? d.DeckDifficulty.UserAdjustment : 0);

    /// <summary>Neutralises user-supplied LIKE wildcards so a term of "%" matches literally instead of everything.</summary>
    private static string EscapeLikeWildcards(string term)
    {
        return term.Replace(LikeEscapeCharacter, LikeEscapeCharacter + LikeEscapeCharacter)
                   .Replace("%", LikeEscapeCharacter + "%")
                   .Replace("_", LikeEscapeCharacter + "_");
    }

    private static int GetLevenshteinMaxDistance(string query)
    {
        return query.Length switch
        {
            <= 5 => 1,
            <= 12 => 2,
            _ => 3
        };
    }

    private async Task<List<DeckIdWithCount>> LevenshteinSuggestionsFallback(string filter, string filterNoSpaces, int limit)
    {
        var maxDist = GetLevenshteinMaxDistance(filter);

        FormattableString sql = $$"""
                                  SELECT DISTINCT ON (dt."DeckId") dt."DeckId", COUNT(*) OVER() AS "TotalCount"
                                  FROM jiten."DeckTitles" dt
                                  JOIN jiten."Decks" d ON dt."DeckId" = d."DeckId"
                                  WHERE d."ParentDeckId" IS NULL
                                    AND (levenshtein(LEFT(LOWER(dt."Title"), 255), LEFT(LOWER({{filter}}), 255)) <= {{maxDist}}
                                      OR levenshtein(LEFT(LOWER(dt."TitleNoSpaces"), 255), LEFT(LOWER({{filterNoSpaces}}), 255)) <= {{maxDist}})
                                  ORDER BY dt."DeckId",
                                           LEAST(
                                               levenshtein(LEFT(LOWER(dt."Title"), 255), LEFT(LOWER({{filter}}), 255)),
                                               levenshtein(LEFT(LOWER(dt."TitleNoSpaces"), 255), LEFT(LOWER({{filterNoSpaces}}), 255))
                                           ) ASC,
                                           LENGTH(dt."Title") ASC
                                  LIMIT {{limit}}
                                  """;

        return await context.Database.SqlQuery<DeckIdWithCount>(sql).ToListAsync();
    }

    private async Task<List<int>> LevenshteinDeckIdsFallback(string filter, string filterNoSpaces)
    {
        var maxDist = GetLevenshteinMaxDistance(filter);

        FormattableString sql = $$"""
                                  SELECT DISTINCT ON (dt."DeckId") dt."DeckId"
                                  FROM jiten."DeckTitles" dt
                                  JOIN jiten."Decks" d ON dt."DeckId" = d."DeckId"
                                  WHERE d."ParentDeckId" IS NULL
                                    AND (levenshtein(LEFT(LOWER(dt."Title"), 255), LEFT(LOWER({{filter}}), 255)) <= {{maxDist}}
                                      OR levenshtein(LEFT(LOWER(dt."TitleNoSpaces"), 255), LEFT(LOWER({{filterNoSpaces}}), 255)) <= {{maxDist}})
                                  ORDER BY dt."DeckId",
                                           LEAST(
                                               levenshtein(LEFT(LOWER(dt."Title"), 255), LEFT(LOWER({{filter}}), 255)),
                                               levenshtein(LEFT(LOWER(dt."TitleNoSpaces"), 255), LEFT(LOWER({{filterNoSpaces}}), 255))
                                           ) ASC,
                                           LENGTH(dt."Title") ASC
                                  """;

        return await context.Database.SqlQuery<int>(sql).ToListAsync();
    }
}

/// <summary>Minimal projection of a top-level deck for sitemap generation.</summary>
public record MediaDeckSitemapEntry(int Id, DateTimeOffset LastUpdate, string CoverName);
