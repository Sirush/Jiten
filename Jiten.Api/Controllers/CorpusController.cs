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
    ICdnService cdnService,
    ILogger<CorpusController> logger)
    : ControllerBase
{
    // PGroonga full-text phrase match operator. (Regex search via &~ was removed: its per-row text
    // scan is inherently too slow on this corpus. Enter individual forms as separate phrase terms.)
    private const string Match = "&@";

    // Strips inline furigana annotations: {漢字'かんじ} -> 漢字. Mirrors FuriganaHintExtractor.
    private const string CleanRawText = """jiten.strip_furigana(drt."RawText")""";

    // Effective release date of a deck's work: the parent's date for a sub-deck (which stores a
    // default date), else the deck's own. Used so the year filter operates at the work level.
    private const string WorkReleaseDate =
        """COALESCE((SELECT w."ReleaseDate" FROM jiten."Decks" w WHERE w."DeckId" = d."ParentDeckId"), d."ReleaseDate")""";

    // Matched CTE: DeckId + per-deck occurrence count via pgroonga_score, which Groonga computes as
    // term frequency (occurrence count) straight from the index — no heap text reads, so it stays
    // fast even for very common terms. Because the index is on jiten.strip_furigana("RawText"), the
    // score already reflects the furigana-stripped text.
    private static readonly string MatchedCte = $"""
        WITH matched AS MATERIALIZED (
            SELECT drt."DeckId", pgroonga_score(drt.tableoid, drt.ctid)::int AS occ
            FROM jiten."DeckRawTexts" drt
            WHERE {CleanRawText} {Match} @term
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

    [HttpPost("publish")]
    public async Task<IActionResult> CorpusPublish([FromBody] CorpusSearchRequest request)
    {
        var validationError = ValidateCorpusRequest(request);
        if (validationError != null) return validationError;

        var searchResponse = await BuildSearchResponse(request);

        var coOccurrences = new List<CorpusCoOccurrence>();
        if (request.Terms.Count > 1)
            coOccurrences = await GetCoOccurrences(request.Terms, request);

        var html = CorpusReportService.GenerateReport(searchResponse, coOccurrences, request);
        var fileName = BuildPublishFileName(request.Terms[0]);

        var url = await cdnService.UploadFile(System.Text.Encoding.UTF8.GetBytes(html), fileName);
        return Ok(new { url });
    }

    // corpus/<sanitised-word>_<randomid>.html. Strips characters unsafe for storage paths / URLs
    // while keeping kana/kanji intact, and caps length to keep the filename reasonable.
    private static string BuildPublishFileName(string term)
    {
        const string unsafeChars = "/\\?#%&:*\"<>| ";
        var safe = new string(term.Where(c => !char.IsControl(c) && !unsafeChars.Contains(c)).ToArray());
        if (safe.Length > 40) safe = safe[..40];
        if (string.IsNullOrEmpty(safe)) safe = "corpus";
        var id = Guid.NewGuid().ToString("N")[..8];
        return $"corpus/{safe}_{id}.html";
    }

    [HttpPost("co-occurrences")]
    public async Task<IActionResult> CorpusCoOccurrences([FromBody] CorpusSearchRequest request)
    {
        if (request.Terms is not { Count: >= 2 and <= 10 })
            return BadRequest("Provide 2-10 terms for co-occurrence analysis.");

        var coOccurrences = await GetCoOccurrences(request.Terms, request);
        return Ok(coOccurrences);
    }

    private async Task<CorpusSearchResponse> BuildSearchResponse(CorpusSearchRequest request)
    {
        var statsTask = GetCorpusStats();
        var scopeTask = GetFilteredScope(request);
        var resultsTask = SearchTermsParallel(request);
        await Task.WhenAll(statsTask, scopeTask, resultsTask);
        return new CorpusSearchResponse
        {
            Results = resultsTask.Result,
            CorpusStats = statsTask.Result,
            FilteredScope = scopeTask.Result
        };
    }

    // Counts the searchable decks (those with raw text) that satisfy the request's filters, plus
    // their distinct works and total characters — the scope the terms are actually searched within.
    private async Task<CorpusFilteredScope> GetFilteredScope(CorpusSearchRequest request)
    {
        var filters = BuildFilterClauses(request);
        var hasFilters = filters.sql.Length > 0;

        var connString = config.GetConnectionString("JitenDatabase")!;
        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        var sql = $"""
            SELECT COUNT(*)::int AS decks,
                   COUNT(DISTINCT COALESCE(d."ParentDeckId", d."DeckId"))::int AS works,
                   COALESCE(SUM(d."CharacterCount"), 0)::bigint AS chars
            FROM jiten."Decks" d
            JOIN jiten."DeckRawTexts" drt ON drt."DeckId" = d."DeckId"
            WHERE 1=1
            {filters.sql}
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 30;
        foreach (var p in filters.parameters)
            cmd.Parameters.Add(CloneParameter(p));

        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new CorpusFilteredScope
        {
            HasFilters = hasFilters,
            Decks = reader.GetInt32(0),
            Works = reader.GetInt32(1),
            Characters = reader.GetInt64(2)
        };
    }

    private async Task<List<CorpusTermResult>> SearchTermsParallel(CorpusSearchRequest request)
    {
        var connString = config.GetConnectionString("JitenDatabase")!;

        Dictionary<int, long> yearTotals;
        Dictionary<int, long> mediaTotals;
        int worksTotal;
        await using (var conn = new NpgsqlConnection(connString))
        {
            await conn.OpenAsync();
            yearTotals = await GetYearTotals(conn);
            mediaTotals = await GetMediaTotals(conn, request);
            worksTotal = await GetWorksTotal(conn, request);
        }

        var tasks = request.Terms.Select(async term =>
        {
            await using var conn = new NpgsqlConnection(connString);
            await conn.OpenAsync();
            await DisableSeqScan(conn);
            return await SearchTerm(conn, term, request, yearTotals, mediaTotals, worksTotal);
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

    // Total characters per media type within the filtered scope (decks that actually have raw text,
    // satisfying the request's filters). Denominator for true occurrences-per-million-characters
    // frequency and for the dispersion register sizes — so both reflect the filtered range.
    private async Task<Dictionary<int, long>> GetMediaTotals(System.Data.Common.DbConnection conn, CorpusSearchRequest request)
    {
        var filters = BuildFilterClauses(request);
        var dict = new Dictionary<int, long>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT d."MediaType"::int AS media, SUM(d."CharacterCount")::bigint AS total_chars
            FROM jiten."Decks" d
            JOIN jiten."DeckRawTexts" drt ON drt."DeckId" = d."DeckId"
            WHERE 1=1
            {filters.sql}
            GROUP BY d."MediaType"
            """;
        cmd.CommandTimeout = 30;
        foreach (var p in filters.parameters)
            cmd.Parameters.Add(CloneParameter(p));
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            dict[reader.GetInt32(0)] = reader.GetInt64(1);
        return dict;
    }

    // Distinct works (top-level series) within the filtered scope — sub-decks collapse to their
    // parent. Denominator for the work-range percentage, so it is relative to the filtered range.
    private async Task<int> GetWorksTotal(System.Data.Common.DbConnection conn, CorpusSearchRequest request)
    {
        var filters = BuildFilterClauses(request);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(DISTINCT COALESCE(d."ParentDeckId", d."DeckId"))
            FROM jiten."Decks" d
            JOIN jiten."DeckRawTexts" drt ON drt."DeckId" = d."DeckId"
            WHERE 1=1
            {filters.sql}
            """;
        cmd.CommandTimeout = 30;
        foreach (var p in filters.parameters)
            cmd.Parameters.Add(CloneParameter(p));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<CorpusTermResult> SearchTerm(System.Data.Common.DbConnection conn, string term, CorpusSearchRequest request,
                                                    Dictionary<int, long> yearTotals, Dictionary<int, long> mediaTotals, int worksTotal)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var filterClauses = BuildFilterClauses(request);

        var snippets = await GetSnippets(conn, term, filterClauses, request);
        logger.LogDebug("[Corpus] '{Term}' snippets: {Ms}ms", term, sw.ElapsedMilliseconds);

        // Single pass over the matching decks: returns per-deck occurrence counts + metadata (no
        // raw text). Everything below is aggregated in-process, avoiding repeated text scans.
        var rows = await GetTermDecks(conn, term, filterClauses);
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

        // Range / dispersion. WorksMatched collapses sub-decks (chapters/episodes) to their work, so
        // 500 hits in one 30-chapter novel count as a single work — the honest "how widely used" signal.
        var worksMatched = rows.Select(r => r.WorkId).Distinct().Count();
        var workRangePct = worksTotal > 0 ? worksMatched * 100.0 / worksTotal : 0;
        var dispersion = ComputeDispersion(mediaBreakdown, mediaTotals, totalOccurrences);

        // Denominator: characters within the filtered scope (mediaTotals already reflects the media,
        // difficulty and year filters), so occ-per-million is relative to the filtered range.
        long corpusChars = mediaTotals.Values.Sum();

        return new CorpusTermResult
        {
            Term = term,
            MatchingDecks = totalDecks,
            TotalOccurrences = totalOccurrences,
            HitsPerMillion = corpusChars > 0 ? totalOccurrences / (corpusChars / 1_000_000.0) : 0,
            WorksMatched = worksMatched,
            WorksTotal = worksTotal,
            WorkRangePercentage = workRangePct,
            Dispersion = dispersion,
            Snippets = snippets,
            MediaBreakdown = mediaBreakdown,
            Trends = trends,
            DifficultyDistribution = difficultyDistribution,
            TopDecks = topDecks,
            DialogueWeightedAvg = dialogueAvg
        };
    }

    // Gries' Deviation of Proportions across media types: 0 = the term is spread exactly in
    // proportion to each register's size, 1 = entirely concentrated in one register. Computed from
    // data already in hand (per-media occurrences vs per-media corpus size) — no extra query.
    private static double ComputeDispersion(List<CorpusMediaBreakdown> mediaBreakdown,
                                            Dictionary<int, long> mediaTotals, long totalOccurrences)
    {
        if (totalOccurrences == 0) return 0;
        double corpusTotalChars = mediaTotals.Values.Sum();
        if (corpusTotalChars == 0) return 0;

        double sumAbs = 0;
        foreach (var (mediaTypeInt, corpusChars) in mediaTotals)
        {
            var observed = mediaBreakdown.FirstOrDefault(m => (int)m.MediaType == mediaTypeInt)?.Occurrences ?? 0;
            var expectedProportion = corpusChars / corpusTotalChars;
            var observedProportion = observed / (double)totalOccurrences;
            sumAbs += Math.Abs(observedProportion - expectedProportion);
        }
        return sumAbs / 2.0;
    }

    private sealed record TermDeckRow(
        int DeckId, int WorkId, int MediaType, int CharacterCount, float Difficulty,
        float DialoguePercentage, int? Year, string Title, string? ParentTitle, int Occ);

    private async Task<List<TermDeckRow>> GetTermDecks(
        System.Data.Common.DbConnection conn, string term,
        (string sql, List<NpgsqlParameter> parameters) filters)
    {
        var sql = $"""
            {MatchedCte}
            SELECT
                d."DeckId",
                COALESCE(d."ParentDeckId", d."DeckId") AS work_id,
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
                WorkId: reader.GetInt32(1),
                MediaType: reader.GetInt32(2),
                CharacterCount: reader.GetInt32(3),
                Difficulty: reader.GetFloat(4),
                DialoguePercentage: reader.GetFloat(5),
                Year: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                Title: reader.GetString(7),
                ParentTitle: reader.IsDBNull(8) ? null : reader.GetString(8),
                Occ: reader.GetInt32(9)));
        }

        return rows;
    }

    private async Task<List<CorpusSnippet>> GetSnippets(
        System.Data.Common.DbConnection conn, string term,
        (string sql, List<NpgsqlParameter> parameters) filters, CorpusSearchRequest request)
    {
        // No materialised CTE: LIMIT lets Postgres stop after @limit index matches instead of
        // pulling the full raw text of every matching deck into memory. We over-fetch a bounded
        // window and reduce to one citation per work below (kills chapter/episode/reprint repeats),
        // which keeps the query fast while making the concordance citation-worthy.
        var sql = $"""
            SELECT
                pgroonga_snippet_html({CleanRawText}, ARRAY[@term], 160) AS snippets,
                left({CleanRawText}, 200) AS fallback,
                d."DeckId",
                COALESCE(d."ParentDeckId", d."DeckId") AS work_id,
                COALESCE(d."OriginalTitle", 'Unknown') AS "DeckTitle",
                p."OriginalTitle" AS parent_title,
                d."MediaType",
                d."Difficulty",
                CASE WHEN d."ReleaseDate" IS NOT NULL AND isfinite(d."ReleaseDate") THEN EXTRACT(YEAR FROM d."ReleaseDate")::int ELSE 0 END AS "ReleaseYear"
            FROM jiten."DeckRawTexts" drt
            JOIN jiten."Decks" d ON d."DeckId" = drt."DeckId"
            LEFT JOIN jiten."Decks" p ON p."DeckId" = d."ParentDeckId"
            WHERE {CleanRawText} {Match} @term
            {filters.sql}
            LIMIT @limit
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;

        // Over-fetch so the per-work / duplicate reduction still yields up to MaxSnippets rows.
        var fetchLimit = Math.Min(request.MaxSnippets * 5, 250);
        cmd.Parameters.Add(new NpgsqlParameter("@term", term));
        cmd.Parameters.Add(new NpgsqlParameter("@limit", fetchLimit));
        foreach (var p in filters.parameters)
            cmd.Parameters.Add(CloneParameter(p));

        var snippets = new List<CorpusSnippet>();
        var seenWorks = new HashSet<int>();
        var seenText = new HashSet<string>();

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var snippetArray = reader.GetFieldValue<string[]>(0);
            var fallback = reader.IsDBNull(1) ? "" : reader.GetString(1);
            // pgroonga_snippet_html normally returns the highlighted match; guard against an empty
            // array (no snippet produced) by falling back to a plain escaped excerpt.
            var html = snippetArray.Length > 0 ? snippetArray[0] : System.Net.WebUtility.HtmlEncode(fallback);
            var text = StripHtml(html);

            var workId = reader.GetInt32(3);
            if (!seenWorks.Add(workId)) continue;            // one citation per work
            if (!seenText.Add(text.Trim())) continue;        // drop identical lines across works

            snippets.Add(new CorpusSnippet
            {
                Html = html,
                Text = text,
                DeckId = reader.GetInt32(2),
                DeckTitle = reader.GetString(4),
                ParentTitle = reader.IsDBNull(5) ? null : reader.GetString(5),
                MediaType = (MediaType)reader.GetInt32(6),
                Difficulty = reader.GetFloat(7),
                ReleaseYear = reader.GetInt32(8)
            });

            if (snippets.Count >= request.MaxSnippets) break;
        }

        return snippets;
    }

    private static readonly System.Text.RegularExpressions.Regex HtmlTagRegex =
        new("<.*?>", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripHtml(string html) =>
        System.Net.WebUtility.HtmlDecode(HtmlTagRegex.Replace(html, ""));

    private async Task<List<CorpusCoOccurrence>> GetCoOccurrences(
        List<string> terms, CorpusSearchRequest request)
    {
        var connString = config.GetConnectionString("JitenDatabase")!;
        var filters = BuildFilterClauses(request);

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
                    WHERE {CleanRawText} {Match} @termA
                      AND {CleanRawText} {Match} @termB
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
        if (request.Terms is not { Count: > 0 and <= 10 })
            return BadRequest("Provide 1-10 search terms.");
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
        var totalWorks = await rawTextSet.Select(rt => rt.Deck.ParentDeckId ?? rt.Deck.DeckId).Distinct().CountAsync();

        return new CorpusStats
        {
            TotalDecks = totalDecks,
            TotalCharacters = totalChars,
            DecksWithRawText = decksWithRawText,
            TotalWorks = totalWorks
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

        // Release year lives on the work (parent) deck; sub-decks (chapters/episodes) carry a default
        // date. Filter on the parent's date when present so a year filter keeps all of a work's
        // sub-decks instead of collapsing the result to standalone/parent decks only.
        if (request.MinReleaseYear.HasValue)
        {
            clauses.Add($"""AND EXTRACT(YEAR FROM {WorkReleaseDate}) >= @minYear""");
            parameters.Add(new NpgsqlParameter("@minYear", request.MinReleaseYear.Value));
        }

        if (request.MaxReleaseYear.HasValue)
        {
            clauses.Add($"""AND EXTRACT(YEAR FROM {WorkReleaseDate}) <= @maxYear""");
            parameters.Add(new NpgsqlParameter("@maxYear", request.MaxReleaseYear.Value));
        }

        return (string.Join("\n", clauses), parameters);
    }

    private static NpgsqlParameter CloneParameter(NpgsqlParameter source) =>
        new(source.ParameterName, source.Value) { NpgsqlDbType = source.NpgsqlDbType };
}
