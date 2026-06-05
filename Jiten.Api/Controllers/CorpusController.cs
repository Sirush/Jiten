using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jiten.Api.Controllers;

/// <summary>
/// Full-text corpus analysis over deck raw texts (PGroonga). Restricted to the Researcher tier
/// (or higher) and administrators via the "RequiresResearcher" policy. Raw texts may contain
/// inline furigana annotations of the form
/// {base'reading}; these are stripped via the jiten.strip_furigana() SQL function (and the
/// matching expression index) so that searching, snippets and occurrence counts operate on the
/// clean prose only.
/// </summary>
[ApiController]
[Route("api/corpus")]
[ApiExplorerSettings(IgnoreApi = true)]
[Authorize("RequiresResearcher")]
public class CorpusController(
    IConfiguration config,
    JitenDbContext dbContext,
    ILogger<CorpusController> logger)
    : ControllerBase
{
    private const string PGroongaExact = "&@";
    private const string PGroongaRegex = "&~";

    // Strips inline furigana annotations: {漢字'かんじ} -> 漢字. Mirrors FuriganaHintExtractor.
    private const string CleanRawText = """jiten.strip_furigana(drt."RawText")""";

    private static string MatchOp(bool useRegex) => useRegex ? PGroongaRegex : PGroongaExact;

    // True per-deck occurrence count on the cleaned text (alias c.clean), not pgroonga_score.
    // Phrase mode counts non-overlapping literal occurrences; regex mode counts regexp matches.
    private static string OccurrenceExpr(bool useRegex) => useRegex
        ? "(SELECT COUNT(*) FROM regexp_matches(c.clean, @term, 'g'))::int"
        : "((char_length(c.clean) - char_length(replace(c.clean, @term, ''))) / NULLIF(char_length(@term), 0))::int";

    // Matched CTE: DeckId + true occurrence count, without materialising any raw text.
    private static string MatchedCte(string matchOp, bool useRegex) => $"""
        WITH matched AS MATERIALIZED (
            SELECT drt."DeckId", {OccurrenceExpr(useRegex)} AS occ
            FROM jiten."DeckRawTexts" drt
            CROSS JOIN LATERAL (SELECT {CleanRawText} AS clean) c
            WHERE {CleanRawText} {matchOp} @term
        )
        """;

    private static async Task DisableSeqScan(NpgsqlConnection conn)
    {
        // Session-level (NOT "SET LOCAL", which is a no-op outside an explicit transaction).
        // The connection is short-lived and disposed per query batch, so this is safe.
        await using var setCmd = conn.CreateCommand();
        setCmd.CommandText = "SET enable_seqscan = off";
        await setCmd.ExecuteNonQueryAsync();
    }

    [HttpPost("search")]
    public async Task<IActionResult> CorpusSearch([FromBody] CorpusSearchRequest request)
    {
        var validationError = ValidateCorpusRequest(request);
        if (validationError != null) return validationError;

        return Ok(await BuildSearchResponse(request));
    }

    [HttpPost("export")]
    public async Task<IActionResult> CorpusExport([FromBody] CorpusSearchRequest request)
    {
        var validationError = ValidateCorpusRequest(request);
        if (validationError != null) return validationError;

        var searchResponse = await BuildSearchResponse(request);

        var coOccurrences = new List<CorpusCoOccurrence>();
        if (request.Terms.Count > 1)
            coOccurrences = await GetCoOccurrences(request.Terms, request);

        var html = CorpusReportService.GenerateReport(searchResponse, coOccurrences, request);
        return File(System.Text.Encoding.UTF8.GetBytes(html), "text/html", $"corpus-report-{DateTime.UtcNow:yyyyMMdd-HHmmss}.html");
    }

    [HttpPost("co-occurrences")]
    public async Task<IActionResult> CorpusCoOccurrences([FromBody] CorpusSearchRequest request)
    {
        if (request.Terms is not { Count: >= 2 and <= 5 })
            return BadRequest("Provide 2-5 terms for co-occurrence analysis.");

        var coOccurrences = await GetCoOccurrences(request.Terms, request);
        return Ok(coOccurrences);
    }

    private async Task<CorpusSearchResponse> BuildSearchResponse(CorpusSearchRequest request)
    {
        var statsTask = GetCorpusStats();
        var resultsTask = SearchTermsParallel(request);
        await Task.WhenAll(statsTask, resultsTask);
        return new CorpusSearchResponse
        {
            Results = resultsTask.Result,
            CorpusStats = statsTask.Result
        };
    }

    private async Task<List<CorpusTermResult>> SearchTermsParallel(CorpusSearchRequest request)
    {
        var connString = config.GetConnectionString("JitenDatabase")!;

        Dictionary<int, long> yearTotals;
        Dictionary<int, long> mediaTotals;
        await using (var conn = new NpgsqlConnection(connString))
        {
            await conn.OpenAsync();
            yearTotals = await GetYearTotals(conn);
            mediaTotals = await GetMediaTotals(conn);
        }

        var tasks = request.Terms.Select(async term =>
        {
            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync();
            await DisableSeqScan(conn);
            return await SearchTerm(conn, term, request, yearTotals, mediaTotals);
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    private static async Task<Dictionary<int, long>> GetYearTotals(System.Data.Common.DbConnection conn)
    {
        var dict = new Dictionary<int, long>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EXTRACT(YEAR FROM d."ReleaseDate")::int AS yr, SUM(d."CharacterCount")::bigint AS total_chars
            FROM jiten."Decks" d
            JOIN jiten."DeckRawTexts" drt ON drt."DeckId" = d."DeckId"
            WHERE d."ReleaseDate" > '0001-01-01' AND isfinite(d."ReleaseDate")
            GROUP BY EXTRACT(YEAR FROM d."ReleaseDate")
            """;
        cmd.CommandTimeout = 30;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            dict[reader.GetInt32(0)] = reader.GetInt64(1);
        return dict;
    }

    // Total characters per media type across the whole corpus (decks that actually have raw text).
    // Used as the denominator for true occurrences-per-million-characters frequency.
    private static async Task<Dictionary<int, long>> GetMediaTotals(System.Data.Common.DbConnection conn)
    {
        var dict = new Dictionary<int, long>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT d."MediaType"::int AS media, SUM(d."CharacterCount")::bigint AS total_chars
            FROM jiten."Decks" d
            JOIN jiten."DeckRawTexts" drt ON drt."DeckId" = d."DeckId"
            GROUP BY d."MediaType"
            """;
        cmd.CommandTimeout = 30;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            dict[reader.GetInt32(0)] = reader.GetInt64(1);
        return dict;
    }

    private async Task<CorpusTermResult> SearchTerm(System.Data.Common.DbConnection conn, string term, CorpusSearchRequest request,
                                                    Dictionary<int, long> yearTotals, Dictionary<int, long> mediaTotals)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var matchOperator = MatchOp(request.UseRegex);
        var filterClauses = BuildFilterClauses(request);

        var snippets = await GetSnippets(conn, term, matchOperator, filterClauses, request);
        logger.LogDebug("[Corpus] '{Term}' snippets: {Ms}ms", term, sw.ElapsedMilliseconds);

        // Single pass over the matching decks: returns per-deck occurrence counts + metadata (no
        // raw text). Everything below is aggregated in-process, avoiding repeated text scans.
        var rows = await GetTermDecks(conn, term, matchOperator, filterClauses, request.UseRegex);
        logger.LogDebug("[Corpus] '{Term}' rows: {Ms}ms ({Count} decks)", term, sw.ElapsedMilliseconds, rows.Count);

        var totalDecks = rows.Count;
        var totalOccurrences = rows.Sum(r => (long)r.Occ);

        var mediaBreakdown = rows
            .GroupBy(r => r.MediaType)
            .Select(g =>
            {
                var occ = g.Sum(r => (long)r.Occ);
                var corpusMediaChars = mediaTotals.GetValueOrDefault(g.Key);
                return new CorpusMediaBreakdown
                {
                    MediaType = (MediaType)g.Key,
                    DeckCount = g.Count(),
                    TotalCharacters = g.Sum(r => (long)r.CharacterCount),
                    Occurrences = occ,
                    HitsPerMillion = corpusMediaChars > 0 ? occ / (corpusMediaChars / 1_000_000.0) : 0,
                    Percentage = totalDecks > 0 ? g.Count() * 100.0 / totalDecks : 0
                };
            })
            .OrderByDescending(m => m.Occurrences)
            .ToList();

        var difficultyDistribution = rows
            .Where(r => r.Difficulty > 0)
            .GroupBy(r => (int)Math.Floor(r.Difficulty))
            .OrderBy(g => g.Key)
            .Select(g => new CorpusDifficultyBucket { BucketMin = g.Key, BucketMax = g.Key + 1, DeckCount = g.Count() })
            .ToList();

        const long minCharsPerYear = 500_000;
        var trends = rows
            .Where(r => r.Year.HasValue)
            .GroupBy(r => r.Year!.Value)
            .OrderBy(g => g.Key)
            .Select(g => (Year: g.Key, Occ: g.Sum(r => (long)r.Occ)))
            .Where(t => yearTotals.TryGetValue(t.Year, out var total) && total >= minCharsPerYear)
            .Select(t =>
            {
                var total = yearTotals[t.Year];
                return new CorpusTrendPoint
                {
                    Year = t.Year,
                    Occurrences = t.Occ,
                    TotalCharsInYear = total,
                    Percentage = t.Occ / (total / 1_000_000.0)
                };
            })
            .ToList();

        var topDecks = rows
            .OrderByDescending(r => r.Occ)
            .Take(20)
            .Select(r => new CorpusTopDeck
            {
                DeckId = r.DeckId,
                Title = r.Title,
                ParentTitle = r.ParentTitle,
                MediaType = (MediaType)r.MediaType,
                Occurrences = r.Occ,
                PerMillion = r.CharacterCount > 0 ? r.Occ / (r.CharacterCount / 1_000_000.0) : 0
            })
            .ToList();

        var dialogueAvg = rows.Count > 0 ? rows.Average(r => (double)r.DialoguePercentage) : 0;

        // Denominator: whole-corpus characters of the media types in scope (respects a media-type
        // filter; difficulty/year filters are not reflected in the denominator, like year totals).
        long corpusChars = request.MediaTypes is { Count: > 0 }
            ? request.MediaTypes.Sum(m => mediaTotals.GetValueOrDefault((int)m))
            : mediaTotals.Values.Sum();

        return new CorpusTermResult
        {
            Term = term,
            MatchingDecks = totalDecks,
            TotalOccurrences = totalOccurrences,
            HitsPerMillion = corpusChars > 0 ? totalOccurrences / (corpusChars / 1_000_000.0) : 0,
            Snippets = snippets,
            MediaBreakdown = mediaBreakdown,
            Trends = trends,
            DifficultyDistribution = difficultyDistribution,
            TopDecks = topDecks,
            DialogueWeightedAvg = dialogueAvg
        };
    }

    private sealed record TermDeckRow(
        int DeckId, int MediaType, int CharacterCount, float Difficulty,
        float DialoguePercentage, int? Year, string Title, string? ParentTitle, int Occ);

    private async Task<List<TermDeckRow>> GetTermDecks(
        System.Data.Common.DbConnection conn, string term, string matchOp,
        (string sql, List<NpgsqlParameter> parameters) filters, bool useRegex)
    {
        var sql = $"""
            {MatchedCte(matchOp, useRegex)}
            SELECT
                d."DeckId",
                d."MediaType"::int AS media,
                d."CharacterCount",
                d."Difficulty",
                d."DialoguePercentage",
                CASE WHEN d."ReleaseDate" > '0001-01-01' AND isfinite(d."ReleaseDate")
                     THEN EXTRACT(YEAR FROM d."ReleaseDate")::int ELSE NULL END AS yr,
                COALESCE(d."OriginalTitle", 'Unknown') AS title,
                p."OriginalTitle" AS parent_title,
                m.occ
            FROM matched m
            JOIN jiten."Decks" d ON d."DeckId" = m."DeckId"
            LEFT JOIN jiten."Decks" p ON p."DeckId" = d."ParentDeckId"
            WHERE m.occ > 0
            {filters.sql}
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        cmd.Parameters.Add(new NpgsqlParameter("@term", term));
        foreach (var p in filters.parameters)
            cmd.Parameters.Add(CloneParameter(p));

        var rows = new List<TermDeckRow>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new TermDeckRow(
                DeckId: reader.GetInt32(0),
                MediaType: reader.GetInt32(1),
                CharacterCount: reader.GetInt32(2),
                Difficulty: reader.GetFloat(3),
                DialoguePercentage: reader.GetFloat(4),
                Year: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                Title: reader.GetString(6),
                ParentTitle: reader.IsDBNull(7) ? null : reader.GetString(7),
                Occ: reader.GetInt32(8)));
        }

        return rows;
    }

    private async Task<List<CorpusSnippet>> GetSnippets(
        System.Data.Common.DbConnection conn, string term, string matchOp,
        (string sql, List<NpgsqlParameter> parameters) filters, CorpusSearchRequest request)
    {
        // No materialised CTE: LIMIT lets Postgres stop after @limit index matches instead of
        // pulling the full raw text of every matching deck into memory.
        var sql = $"""
            SELECT
                pgroonga_snippet_html({CleanRawText}, ARRAY[@term], 160) AS snippets,
                left({CleanRawText}, 200) AS fallback,
                d."DeckId",
                COALESCE(d."OriginalTitle", 'Unknown') AS "DeckTitle",
                d."MediaType",
                d."Difficulty",
                CASE WHEN d."ReleaseDate" IS NOT NULL AND isfinite(d."ReleaseDate") THEN EXTRACT(YEAR FROM d."ReleaseDate")::int ELSE 0 END AS "ReleaseYear"
            FROM jiten."DeckRawTexts" drt
            JOIN jiten."Decks" d ON d."DeckId" = drt."DeckId"
            WHERE {CleanRawText} {matchOp} @term
            {filters.sql}
            LIMIT @limit
            """;

        var snippets = new List<CorpusSnippet>();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;

        cmd.Parameters.Add(new NpgsqlParameter("@term", term));
        cmd.Parameters.Add(new NpgsqlParameter("@limit", request.MaxSnippets));
        foreach (var p in filters.parameters)
            cmd.Parameters.Add(CloneParameter(p));

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var snippetArray = reader.GetFieldValue<string[]>(0);
            var fallback = reader.IsDBNull(1) ? "" : reader.GetString(1);
            // Regex mode passes the regex string as a literal keyword, so pgroonga_snippet_html
            // returns an empty array (no highlight) — fall back to a plain escaped excerpt.
            var html = snippetArray.Length > 0 ? snippetArray[0] : System.Net.WebUtility.HtmlEncode(fallback);

            snippets.Add(new CorpusSnippet
            {
                Html = html,
                DeckId = reader.GetInt32(2),
                DeckTitle = reader.GetString(3),
                MediaType = (MediaType)reader.GetInt32(4),
                Difficulty = reader.GetFloat(5),
                ReleaseYear = reader.GetInt32(6)
            });
        }

        return snippets;
    }

    private async Task<List<CorpusCoOccurrence>> GetCoOccurrences(
        List<string> terms, CorpusSearchRequest request)
    {
        var connString = config.GetConnectionString("JitenDatabase")!;
        var filters = BuildFilterClauses(request);
        var matchOp = MatchOp(request.UseRegex);

        var pairs = new List<(int i, int j)>();
        for (int i = 0; i < terms.Count; i++)
            for (int j = i + 1; j < terms.Count; j++)
                pairs.Add((i, j));

        var tasks = pairs.Select(async pair =>
        {
            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync();
            await DisableSeqScan(conn);

            var sql = $"""
                WITH matched AS MATERIALIZED (
                    SELECT drt."DeckId"
                    FROM jiten."DeckRawTexts" drt
                    WHERE {CleanRawText} {matchOp} @termA
                      AND {CleanRawText} {matchOp} @termB
                )
                SELECT COUNT(*)::int
                FROM matched m
                JOIN jiten."Decks" d ON d."DeckId" = m."DeckId"
                WHERE 1=1
                {filters.sql}
                """;

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 60;
            cmd.Parameters.Add(new NpgsqlParameter("@termA", terms[pair.i]));
            cmd.Parameters.Add(new NpgsqlParameter("@termB", terms[pair.j]));
            foreach (var p in filters.parameters)
                cmd.Parameters.Add(CloneParameter(p));

            var count = (int)(await cmd.ExecuteScalarAsync())!;
            return new CorpusCoOccurrence
            {
                TermA = terms[pair.i],
                TermB = terms[pair.j],
                SharedDecks = count
            };
        });

        return (await Task.WhenAll(tasks)).ToList();
    }

    private IActionResult? ValidateCorpusRequest(CorpusSearchRequest request)
    {
        if (request.Terms is not { Count: > 0 and <= 5 })
            return BadRequest("Provide 1-5 search terms.");
        if (request.Terms.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 200))
            return BadRequest("Each term must be 1-200 characters.");
        request.MaxSnippets = Math.Clamp(request.MaxSnippets, 1, 50);
        return null;
    }

    private async Task<CorpusStats> GetCorpusStats()
    {
        var rawTextSet = dbContext.Set<DeckRawText>();
        // Only decks that actually have raw text are part of the searchable corpus; summing all
        // decks would double-count, since parent decks aggregate CharacterCount = Σ children.
        var totalChars = await rawTextSet.SumAsync(rt => (long)rt.Deck.CharacterCount);
        var decksWithRawText = await rawTextSet.CountAsync();
        var totalDecks = await dbContext.Decks.CountAsync();

        return new CorpusStats
        {
            TotalDecks = totalDecks,
            TotalCharacters = totalChars,
            DecksWithRawText = decksWithRawText
        };
    }

    private (string sql, List<NpgsqlParameter> parameters) BuildFilterClauses(CorpusSearchRequest request)
    {
        var clauses = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        if (request.MediaTypes is { Count: > 0 })
        {
            clauses.Add("""AND d."MediaType" = ANY(@mediaTypes)""");
            parameters.Add(new NpgsqlParameter("@mediaTypes", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer)
                { Value = request.MediaTypes.Select(m => (int)m).ToArray() });
        }

        if (request.MinDifficulty.HasValue)
        {
            clauses.Add("""AND d."Difficulty" >= @minDiff""");
            parameters.Add(new NpgsqlParameter("@minDiff", request.MinDifficulty.Value));
        }

        if (request.MaxDifficulty.HasValue)
        {
            clauses.Add("""AND d."Difficulty" <= @maxDiff""");
            parameters.Add(new NpgsqlParameter("@maxDiff", request.MaxDifficulty.Value));
        }

        if (request.MinReleaseYear.HasValue)
        {
            clauses.Add("""AND EXTRACT(YEAR FROM d."ReleaseDate") >= @minYear""");
            parameters.Add(new NpgsqlParameter("@minYear", request.MinReleaseYear.Value));
        }

        if (request.MaxReleaseYear.HasValue)
        {
            clauses.Add("""AND EXTRACT(YEAR FROM d."ReleaseDate") <= @maxYear""");
            parameters.Add(new NpgsqlParameter("@maxYear", request.MaxReleaseYear.Value));
        }

        return (string.Join("\n", clauses), parameters);
    }

    private static NpgsqlParameter CloneParameter(NpgsqlParameter source) =>
        new(source.ParameterName, source.Value) { NpgsqlDbType = source.NpgsqlDbType };
}
