using Hangfire;
using Jiten.Api.Dtos;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Enums;
using Jiten.Api.Helpers;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/word-sets")]
[Produces("application/json")]
public class WordSetController(
    JitenDbContext jitenContext,
    UserDbContext userContext,
    ICurrentUserService currentUserService,
    IFrequencySourceResolver frequencySource,
    IBackgroundJobClient backgroundJobs,
    ILogger<WordSetController> logger) : ControllerBase
{
    [HttpGet]
    [SwaggerOperation(Summary = "Get all word sets", Description = "Returns all available word sets with word and form counts.")]
    [ProducesResponseType(typeof(List<WordSetDto>), StatusCodes.Status200OK)]
    public async Task<IResult> GetWordSets()
    {
        var sets = await jitenContext.WordSets
            .AsNoTracking()
            .OrderBy(ws => ws.SetId)
            .Select(ws => new WordSetDto
            {
                SetId = ws.SetId,
                Slug = ws.Slug,
                Name = ws.Name,
                Description = ws.Description,
                WordCount = ws.WordCount,
                FormCount = ws.Members.Count
            })
            .ToListAsync();

        return Results.Ok(sets);
    }

    [HttpGet("{slug}")]
    [SwaggerOperation(Summary = "Get word set by slug", Description = "Returns a single word set by its URL-friendly slug.")]
    [ProducesResponseType(typeof(WordSetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IResult> GetWordSet(string slug)
    {
        var set = await jitenContext.WordSets
            .AsNoTracking()
            .Where(ws => ws.Slug == slug)
            .Select(ws => new WordSetDto
            {
                SetId = ws.SetId,
                Slug = ws.Slug,
                Name = ws.Name,
                Description = ws.Description,
                WordCount = ws.WordCount,
                FormCount = ws.Members.Count
            })
            .FirstOrDefaultAsync();

        if (set == null)
            return Results.NotFound("Word set not found");

        return Results.Ok(set);
    }

    [HttpGet("{slug}/vocabulary")]
    [SwaggerOperation(Summary = "Get word set vocabulary", Description = "Returns paginated vocabulary list for a word set with sorting and filtering.")]
    [ProducesResponseType(typeof(PaginatedResponse<List<WordDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IResult> GetWordSetVocabulary(
        string slug,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50,
        [FromQuery] string sortBy = "",
        [FromQuery] SortOrder sortOrder = SortOrder.Ascending,
        [FromQuery] string displayFilter = "all",
        [FromQuery] string? suspended = null,
        [FromQuery] string? redundant = null,
        [FromQuery] string? search = null,
        [FromQuery] string? pos = null,
        [FromQuery] string? excludePos = null,
        [FromQuery] bool hideKanaOnly = false)
    {
        limit = Math.Clamp(limit, 1, 100);

        var set = await jitenContext.WordSets
            .AsNoTracking()
            .FirstOrDefaultAsync(ws => ws.Slug == slug);

        if (set == null)
            return Results.NotFound("Word set not found");

        var baseQuery = jitenContext.WordSetMembers
            .AsNoTracking()
            .Where(wsm => wsm.SetId == set.SetId);

        HashSet<int>? searchWordIds = null;
        if (!string.IsNullOrWhiteSpace(search))
        {
            searchWordIds = await SearchHelper.ResolveSearchWordIds(jitenContext, search);
            baseQuery = baseQuery.Where(wsm => searchWordIds.Contains(wsm.WordId));
        }

        var posTags = VocabularyFilterHelper.ParseCommaSeparatedTags(pos);
        if (posTags.Length > 0)
        {
            var wordIdsWithPos = jitenContext.JMDictWords.AsNoTracking()
                .Where(w => w.PartsOfSpeech.Any(p => posTags.Contains(p)));
            baseQuery = baseQuery.Where(wsm => wordIdsWithPos.Any(w => w.WordId == wsm.WordId));
        }

        var excludePosTags = VocabularyFilterHelper.ParseCommaSeparatedTags(excludePos);
        if (excludePosTags.Length > 0)
        {
            var wordIdsToExclude = jitenContext.JMDictWords.AsNoTracking()
                .Where(w => w.PartsOfSpeech.Any(p => excludePosTags.Contains(p)));
            baseQuery = baseQuery.Where(wsm => !wordIdsToExclude.Any(w => w.WordId == wsm.WordId));
        }

        if (hideKanaOnly)
        {
            baseQuery = baseQuery.Where(wsm => jitenContext.WordForms
                .Any(wf => wf.WordId == wsm.WordId && wf.ReadingIndex == wsm.ReadingIndex
                           && wf.FormType != JmDictFormType.KanaForm));
        }

        var displayFilterSpec = VocabularyDisplayFilter.Parse(displayFilter, suspended, redundant);
        bool needsKnownFilter = currentUserService.IsAuthenticated && displayFilterSpec.IsActive;

        List<WordSetMember> pagedItems;
        int totalCount;

        if (needsKnownFilter && displayFilterSpec.Redundant != ModifierMode.Show)
        {
            (pagedItems, totalCount) = await FilterVocabularyInMemory(baseQuery, displayFilterSpec, sortBy, sortOrder, offset, limit);
        }
        else if (needsKnownFilter)
        {
            (pagedItems, totalCount) = await ExecuteFilteredVocabularyQuery(
                set.SetId, currentUserService.UserId!, displayFilterSpec, sortBy, sortOrder, offset, limit,
                searchWordIds, posTags, excludePosTags, hideKanaOnly);
        }
        else if (sortBy == "globalFreq")
        {
            totalCount = await baseQuery.CountAsync();

            var sorted = sortOrder == SortOrder.Ascending
                ? baseQuery.OrderBy(m => jitenContext.WordFormFrequencies
                    .Where(wff => wff.WordId == m.WordId && wff.ReadingIndex == m.ReadingIndex)
                    .Select(wff => wff.FrequencyRank)
                    .FirstOrDefault()).ThenBy(m => m.Position)
                : baseQuery.OrderByDescending(m => jitenContext.WordFormFrequencies
                    .Where(wff => wff.WordId == m.WordId && wff.ReadingIndex == m.ReadingIndex)
                    .Select(wff => wff.FrequencyRank)
                    .FirstOrDefault()).ThenBy(m => m.Position);

            pagedItems = await sorted.Skip(offset).Take(limit).ToListAsync();
        }
        else
        {
            totalCount = await baseQuery.CountAsync();

            var sorted = sortOrder == SortOrder.Ascending
                ? baseQuery.OrderBy(m => m.Position)
                : baseQuery.OrderByDescending(m => m.Position);

            pagedItems = await sorted.Skip(offset).Take(limit).ToListAsync();
        }

        var pagedWordIds = pagedItems.Select(p => p.WordId).Distinct().ToList();

        var wsFormDict = await WordFormHelper.LoadWordForms(jitenContext, pagedWordIds);
        var wsFormFreqDict = await frequencySource.LoadFrequencies(jitenContext, pagedWordIds);

        var words = await jitenContext.JMDictWords
            .AsNoTracking()
            .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
            .Where(w => pagedWordIds.Contains(w.WordId))
            .ToDictionaryAsync(w => w.WordId);

        var knownWordStates = await currentUserService.GetKnownWordsState(
            pagedItems.Select(p => (p.WordId, (byte)p.ReadingIndex)));

        var vocabulary = pagedItems
            .Where(p => words.ContainsKey(p.WordId))
            .Select(p =>
            {
                var word = words[p.WordId];
                var readingIndex = (byte)p.ReadingIndex;
                var form = wsFormDict.GetValueOrDefault((p.WordId, p.ReadingIndex));

                var mainReading = form != null
                    ? WordFormHelper.ToFormDto(form, wsFormFreqDict.Resolve(p.WordId, p.ReadingIndex))
                    : new WordFormDto { ReadingIndex = readingIndex };

                return new WordDto
                {
                    WordId = p.WordId,
                    MainReading = mainReading,
                    AlternativeReadings = [],
                    Definitions = word.Definitions.ToDefinitionDtos(),
                    PartsOfSpeech = word.PartsOfSpeech,
                    PitchAccents = word.PitchAccents,
                    KnownStates = knownWordStates.GetValueOrDefault((p.WordId, readingIndex), [KnownState.New])
                };
            })
            .ToList();

        logger.LogInformation("GetWordSetVocabulary: Slug={Slug}, Offset={Offset}, Limit={Limit}, SortBy={SortBy}, DisplayFilter={DisplayFilter}, ResultCount={ResultCount}",
                              slug, offset, limit, sortBy, displayFilter, vocabulary.Count);

        return Results.Ok(new PaginatedResponse<List<WordDto>>(vocabulary, totalCount, limit, offset));
    }

    [HttpGet("subscriptions")]
    [Authorize]
    [SwaggerOperation(Summary = "Get user's word set subscriptions", Description = "Returns the current user's word set subscriptions with state.")]
    [ProducesResponseType(typeof(List<UserWordSetSubscriptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IResult> GetSubscriptions()
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var userStates = await userContext.UserWordSetStates
            .AsNoTracking()
            .Where(uwss => uwss.UserId == userId)
            .ToListAsync();

        if (userStates.Count == 0)
            return Results.Ok(new List<UserWordSetSubscriptionDto>());

        var setIds = userStates.Select(s => s.SetId).ToList();

        var sets = await jitenContext.WordSets
            .AsNoTracking()
            .Where(ws => setIds.Contains(ws.SetId))
            .Select(ws => new { ws.SetId, ws.Slug, ws.Name, ws.Description, ws.WordCount, FormCount = ws.Members.Count })
            .ToDictionaryAsync(ws => ws.SetId);

        var subscriptions = userStates
            .Where(s => sets.ContainsKey(s.SetId))
            .Select(s =>
            {
                var ws = sets[s.SetId];
                return new UserWordSetSubscriptionDto
                {
                    SetId = ws.SetId,
                    Slug = ws.Slug,
                    Name = ws.Name,
                    Description = ws.Description,
                    State = s.State,
                    WordCount = ws.WordCount,
                    FormCount = ws.FormCount,
                    SubscribedAt = s.CreatedAt
                };
            })
            .ToList();

        return Results.Ok(subscriptions);
    }

    [HttpPost("{setId:int}/subscribe")]
    [Authorize]
    [SwaggerOperation(Summary = "Subscribe to a word set", Description = "Subscribe to a word set with the specified state (Blacklisted or Mastered).")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IResult> Subscribe(int setId, [FromBody] WordSetSubscribeRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        if (request.State != WordSetStateType.Blacklisted && request.State != WordSetStateType.Mastered)
            return Results.BadRequest("Invalid state. Must be Blacklisted (1) or Mastered (2).");

        var set = await jitenContext.WordSets.AsNoTracking().FirstOrDefaultAsync(ws => ws.SetId == setId);
        if (set == null)
            return Results.NotFound("Word set not found");

        var existing = await userContext.UserWordSetStates
            .FirstOrDefaultAsync(uwss => uwss.UserId == userId && uwss.SetId == setId);

        if (existing != null)
        {
            existing.State = request.State;
        }
        else
        {
            userContext.UserWordSetStates.Add(new UserWordSetState
            {
                UserId = userId,
                SetId = setId,
                State = request.State
            });
        }

        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();

        backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));

        logger.LogInformation("User subscribed to word set: UserId={UserId}, SetId={SetId}, State={State}",
                              userId, setId, request.State);

        return Results.Ok(new { success = true });
    }

    [HttpDelete("{setId:int}/subscribe")]
    [Authorize]
    [SwaggerOperation(Summary = "Unsubscribe from a word set", Description = "Remove subscription from a word set.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IResult> Unsubscribe(int setId)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var subscription = await userContext.UserWordSetStates
            .FirstOrDefaultAsync(uwss => uwss.UserId == userId && uwss.SetId == setId);

        if (subscription == null)
            return Results.NotFound("Subscription not found");

        userContext.UserWordSetStates.Remove(subscription);
        await CoverageDirtyHelper.MarkCoverageDirty(userContext, userId);
        await userContext.SaveChangesAsync();

        backgroundJobs.Enqueue<ComputationJob>(job => job.ComputeUserCoverage(userId));

        logger.LogInformation("User unsubscribed from word set: UserId={UserId}, SetId={SetId}",
                              userId, setId);

        return Results.Ok(new { success = true });
    }

    private class FilteredMemberResult
    {
        public int WordId { get; set; }
        public short ReadingIndex { get; set; }
        public int Position { get; set; }
        public long TotalCount { get; set; }
    }

    /// <summary>
    /// Paginates a word set through <c>GetKnownWordsState</c> instead of the SQL path. Redundancy comes from the
    /// form-sibling and derivation caches, which SQL cannot see, so any filter touching it resolves the set in memory.
    /// </summary>
    private async Task<(List<WordSetMember> Items, int TotalCount)> FilterVocabularyInMemory(
        IQueryable<WordSetMember> baseQuery, VocabularyDisplayFilter filter, string sortBy, SortOrder sortOrder, int offset, int limit)
    {
        var members = await baseQuery.ToListAsync();
        var states = await currentUserService.GetKnownWordsState(members.Select(m => (m.WordId, (byte)m.ReadingIndex)));

        var matching = members
            .Where(m => filter.Matches(states.GetValueOrDefault((m.WordId, (byte)m.ReadingIndex), [KnownState.New])))
            .ToList();

        IEnumerable<WordSetMember> sorted;
        if (sortBy == "globalFreq")
        {
            var freqMap = await WordFormHelper.LoadWordFormFrequencies(jitenContext, matching.Select(m => m.WordId).Distinct().ToList());
            int RankOf(WordSetMember m) => freqMap.TryGetValue((m.WordId, m.ReadingIndex), out var f) ? f.FrequencyRank : int.MaxValue;
            sorted = sortOrder == SortOrder.Ascending
                ? matching.OrderBy(RankOf).ThenBy(m => m.Position)
                : matching.OrderByDescending(RankOf).ThenBy(m => m.Position);
        }
        else
        {
            sorted = sortOrder == SortOrder.Ascending
                ? matching.OrderBy(m => m.Position)
                : matching.OrderByDescending(m => m.Position);
        }

        return (sorted.Skip(offset).Take(limit).ToList(), matching.Count);
    }

    private async Task<(List<WordSetMember> Items, int TotalCount)> ExecuteFilteredVocabularyQuery(
        int setId, string userId, VocabularyDisplayFilter filter, string sortBy, SortOrder sortOrder, int offset, int limit,
        HashSet<int>? searchWordIds = null, string[]? posTags = null, string[]? excludePosTags = null, bool hideKanaOnly = false)
    {
        var userIdGuid = Guid.Parse(userId);

        // Mirrors VocabularyDisplayFilter.ResolveTier: a card outranks word-set membership, an unreviewed card
        // is Learning rather than Unknown, and a suspended card keeps the tier its interval earned it.
        const string activeCard = @"f.""WordId"" IS NOT NULL AND f.""State"" NOT IN (4,5)";
        const string interval = @"(EXTRACT(EPOCH FROM (f.""Due"" - f.""LastReview"")) / 86400.0)";

        var tierClauses = filter.Tiers.Select(tier => tier switch
        {
            VocabularyTier.Unknown =>
                @"(f.""WordId"" IS NULL AND NOT COALESCE(use_s.has_mastered, FALSE) AND NOT COALESCE(use_s.has_blacklisted, FALSE))",
            VocabularyTier.Learning =>
                $@"({activeCard} AND f.""LastReview"" IS NULL)",
            VocabularyTier.Young =>
                $@"({activeCard} AND f.""LastReview"" IS NOT NULL AND {interval} < 21)",
            VocabularyTier.Mature =>
                $@"({activeCard} AND f.""LastReview"" IS NOT NULL AND {interval} >= 21)",
            VocabularyTier.Mastered =>
                @"((f.""WordId"" IS NOT NULL AND f.""State"" = 5) OR (f.""WordId"" IS NULL AND COALESCE(use_s.has_mastered, FALSE)))",
            VocabularyTier.Blacklisted =>
                @"((f.""WordId"" IS NOT NULL AND f.""State"" = 4) OR (f.""WordId"" IS NULL AND COALESCE(use_s.has_blacklisted, FALSE)))",
            _ => "TRUE"
        }).ToList();

        string filterClause = tierClauses.Count > 0 ? string.Join(" OR ", tierClauses) : "TRUE";

        string suspendedClause = filter.Suspended switch
        {
            ModifierMode.Hide => @" AND COALESCE(f.""State"", -1) != 6",
            ModifierMode.Only => @" AND f.""State"" = 6",
            _ => ""
        };

        string freqJoin = sortBy == "globalFreq"
            ? @"LEFT JOIN jmdict.""WordFormFrequencies"" wff ON m.""WordId"" = wff.""WordId"" AND m.""ReadingIndex"" = wff.""ReadingIndex"""
            : "";

        string orderByClause = (sortBy, sortOrder) switch
        {
            ("globalFreq", SortOrder.Ascending) =>
                @"COALESCE(wff.""FrequencyRank"", 2147483647) ASC, m.""Position"" ASC",
            ("globalFreq", _) =>
                @"COALESCE(wff.""FrequencyRank"", 2147483647) DESC, m.""Position"" ASC",
            (_, SortOrder.Ascending) => @"m.""Position"" ASC",
            _ => @"m.""Position"" DESC"
        };

        var paramList = new List<object> { userIdGuid, setId, offset, limit };
        int paramIdx = 4;

        string searchClause = "";
        if (searchWordIds != null)
        {
            searchClause = $@" AND m.""WordId"" = ANY({{{paramIdx}}})";
            paramList.Add(searchWordIds.ToArray());
            paramIdx++;
        }

        string posClause = "";
        if (posTags is { Length: > 0 })
        {
            posClause = $@" AND m.""WordId"" IN (SELECT jw.""WordId"" FROM jmdict.""JMDictWords"" jw WHERE jw.""PartsOfSpeech"" && {{{paramIdx}}})";
            paramList.Add(posTags);
            paramIdx++;
        }

        string excludePosClause = "";
        if (excludePosTags is { Length: > 0 })
        {
            excludePosClause = $@" AND m.""WordId"" NOT IN (SELECT jw.""WordId"" FROM jmdict.""JMDictWords"" jw WHERE jw.""PartsOfSpeech"" && {{{paramIdx}}})";
            paramList.Add(excludePosTags);
            paramIdx++;
        }

        string kanaClause = hideKanaOnly
            ? @" AND EXISTS (SELECT 1 FROM jmdict.""WordForms"" wf WHERE wf.""WordId"" = m.""WordId"" AND wf.""ReadingIndex"" = m.""ReadingIndex"" AND wf.""FormType"" != 1)"
            : "";

        string sql =
            @"WITH user_fsrs AS (
                SELECT ""WordId"", ""ReadingIndex"", ""State"", ""Due"", ""LastReview""
                FROM ""user"".""FsrsCards""
                WHERE ""UserId"" = {0}
                  AND ""WordId"" IN (SELECT ""WordId"" FROM jiten.""WordSetMembers"" WHERE ""SetId"" = {1})
            ),
            user_set_effective AS (
                SELECT wsm.""WordId"", wsm.""ReadingIndex"",
                       BOOL_OR(uwss.""State"" = 2) AS has_mastered,
                       BOOL_OR(uwss.""State"" = 1) AS has_blacklisted
                FROM ""user"".""UserWordSetStates"" uwss
                INNER JOIN jiten.""WordSetMembers"" wsm ON wsm.""SetId"" = uwss.""SetId""
                WHERE uwss.""UserId"" = {0}
                  AND wsm.""WordId"" IN (SELECT ""WordId"" FROM jiten.""WordSetMembers"" WHERE ""SetId"" = {1})
                GROUP BY wsm.""WordId"", wsm.""ReadingIndex""
            )
            SELECT m.""WordId"", m.""ReadingIndex"", m.""Position"", COUNT(*) OVER() AS ""TotalCount""
            FROM jiten.""WordSetMembers"" m
            LEFT JOIN user_fsrs f ON m.""WordId"" = f.""WordId"" AND m.""ReadingIndex"" = f.""ReadingIndex""
            LEFT JOIN user_set_effective use_s ON m.""WordId"" = use_s.""WordId"" AND m.""ReadingIndex"" = use_s.""ReadingIndex""
            " + freqJoin + @"
            WHERE m.""SetId"" = {1} AND (" + filterClause + @")" + suspendedClause + searchClause + posClause + excludePosClause + kanaClause + @"
            ORDER BY " + orderByClause + @"
            OFFSET {2} LIMIT {3}";

        var results = await jitenContext.Database
            .SqlQueryRaw<FilteredMemberResult>(sql, paramList.ToArray())
            .ToListAsync();

        int totalCount = (int)(results.FirstOrDefault()?.TotalCount ?? 0);

        var items = results.Select(r => new WordSetMember
        {
            SetId = setId,
            WordId = r.WordId,
            ReadingIndex = r.ReadingIndex,
            Position = r.Position
        }).ToList();

        return (items, totalCount);
    }
}
