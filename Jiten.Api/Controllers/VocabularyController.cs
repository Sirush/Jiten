using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Utils;
using Jiten.Parser;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;
using Swashbuckle.AspNetCore.Annotations;
using System.Text.Json;
using WanaKanaShaapu;

namespace Jiten.Api.Controllers;

/// <summary>
/// Endpoints for working with vocabulary: words, parsing text, and example sentences.
/// </summary>
[ApiController]
[Route("api/vocabulary")]
[EnableRateLimiting("fixed")]
[Produces("application/json")]
public class VocabularyController(JitenDbContext context, IDbContextFactory<JitenDbContext> contextFactory, ICurrentUserService currentUserService, IDerivationLinkCache derivationCache, UserDbContext userContext, IMemoryCache memoryCache, IConnectionMultiplexer redis, IExampleSentenceQueryService exampleSentences, IFrequencySourceResolver frequencySource) : ControllerBase
{
    /// <summary>
    /// Gets a word by its ID and reading index, including definitions, readings, frequency and user known state.
    /// </summary>
    /// <param name="wordId">The unique identifier of the word.</param>
    /// <param name="readingIndex">Index of the reading to treat as main (zero-based).</param>
    /// <returns>The full word data.</returns>
    [HttpGet("{wordId}/{readingIndex}")]
    [SwaggerOperation(Summary = "Get word by ID and reading index", Description = "Returns a word with main and alternative readings, definitions, parts of speech, pitch accents, frequency and known state.")]
    [ProducesResponseType(typeof(WordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Client)]
    public async Task<IResult> GetWord([FromRoute] int wordId, [FromRoute] byte readingIndex)
    {
        await using var ctx1 = await contextFactory.CreateDbContextAsync();
        await using var ctx2 = await contextFactory.CreateDbContextAsync();
        await using var ctx3 = await contextFactory.CreateDbContextAsync();
        await using var ctx4 = await contextFactory.CreateDbContextAsync();

        var wordTask = ctx1.JMDictWords.AsNoTracking()
                           .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
                           .FirstOrDefaultAsync(w => w.WordId == wordId);

        var wordFormsTask = WordFormHelper.LoadWordFormsForWord(ctx2, wordId);

        var formFreqsTask = ctx3.WordFormFrequencies
            .AsNoTracking()
            .Where(wff => wff.WordId == wordId)
            .ToDictionaryAsync(wff => wff.ReadingIndex);

        var usedInMediaByTypeTask = ctx4.DeckWords.AsNoTracking()
                                             .Where(dw => dw.WordId == wordId && dw.ReadingIndex == readingIndex)
                                             .Join(
                                                   ctx4.Decks.AsNoTracking()
                                                          .Where(d => d.ParentDeckId == null)
                                                          .Select(d => new { d.DeckId, d.MediaType }),
                                                   dw => dw.DeckId,
                                                   d => d.DeckId,
                                                   (dw, d) => d.MediaType
                                                  )
                                             .GroupBy(mediaType => mediaType)
                                             .Select(g => new { MediaType = g.Key, Count = g.Count() })
                                             .ToDictionaryAsync(x => (int)x.MediaType, x => x.Count);

        var knownStatesTask = currentUserService.GetKnownWordState(wordId, readingIndex);

        var composedOfTask = CompositionHelper.LoadComposedOf(contextFactory, wordId, readingIndex);
        var usedInTask = CompositionHelper.LoadUsedIn(contextFactory, wordId, readingIndex, 0, 20);

        await Task.WhenAll(wordTask, wordFormsTask, formFreqsTask, usedInMediaByTypeTask, knownStatesTask, composedOfTask, usedInTask);

        var scope = await frequencySource.Resolve();

        var word = await wordTask;
        if (word == null)
            return Results.NotFound();

        var xrefs = await ctx1.JmDictCrossReferences.AsNoTracking()
                              .Where(x => x.FromWordId == wordId)
                              .ToListAsync();

        var wordForms = await wordFormsTask;
        var mainForm = wordForms.FirstOrDefault(wf => wf.ReadingIndex == readingIndex);
        if (mainForm == null)
            return Results.NotFound();

        var formFreqs = await formFreqsTask;
        var usedInMediaByType = await usedInMediaByTypeTask;

        // Location=Client keeps this payload out of any shared cache, so the caller's own ranking may go in it.
        var scopedFreqs = new ScopedFormFrequencies(
            scope,
            formFreqs.ToDictionary(kv => (wordId, kv.Key), kv => kv.Value),
            scope.MediaType.HasValue ? await WordFormHelper.LoadWordFormFrequencies(ctx3, [wordId], scope.MediaType) : null,
            scope.FrequencyListId.HasValue ? await frequencySource.ListRanks(scope.FrequencyListId.Value) : null);

        var mainReading = WordFormHelper.ToFormDto(mainForm, scopedFreqs.Resolve(wordId, mainForm.ReadingIndex), usedInMediaByType);

        var enabledDerivations = currentUserService.UserId == null
            ? null
            : await DerivationSettingsHelper.GetEnabledCategories(memoryCache, userContext, currentUserService.UserId);
        var (derivedFrom, derives) =
            await DerivationDisplayHelper.Load(contextFactory, derivationCache, wordId, readingIndex, enabledDerivations);
        var redundantVia = (await knownStatesTask).Contains(KnownState.Redundant)
            ? await DerivationDisplayHelper.LoadCover(contextFactory,
                                                      await currentUserService.GetCoveringDerivation(wordId, readingIndex))
            : null;

        List<WordFormDto> alternativeReadings = wordForms
                                                   .Select(form => WordFormHelper.ToPlainFormDto(form, scopedFreqs.Resolve(wordId, form.ReadingIndex)))
                                                   .ToList();

        return Results.Ok(new WordDto
                          {
                              WordId = word.WordId, MainReading = mainReading, AlternativeReadings = alternativeReadings,
                              Definitions = word.Definitions.ToDefinitionDtos(xrefs.ToXrefsBySense()), PartsOfSpeech = word.PartsOfSpeech,
                              PitchAccents = word.PitchAccents, KnownStates = await knownStatesTask,
                              ComposedOf = await composedOfTask,
                              UsedIn = usedInTask.Result.Items,
                              UsedInTotal = usedInTask.Result.Total,
                              LanguageSources = word.LanguageSources.ToDto(),
                              EntryInfo = word.EntryInfo.Count > 0 ? word.EntryInfo.Select(e => e.Text).ToList() : null,
                              DerivedFrom = derivedFrom.Count > 0 ? derivedFrom : null,
                              Derives = derives.Count > 0 ? derives : null,
                              RedundantVia = redundantVia
                          });
    }

    [HttpGet("{wordId}/{readingIndex}/info")]
    [SwaggerOperation(Summary = "Get word info (no user state)", Description = "Returns word data without user-specific known state or media frequency breakdown. Publicly cached.")]
    [ProducesResponseType(typeof(WordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ResponseCache(Duration = 3600)]
    public async Task<IResult> GetWordInfo([FromRoute] int wordId, [FromRoute] byte readingIndex)
    {
        await using var ctx1 = await contextFactory.CreateDbContextAsync();
        await using var ctx2 = await contextFactory.CreateDbContextAsync();

        var wordTask = context.JMDictWords.AsNoTracking()
                              .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
                              .FirstOrDefaultAsync(w => w.WordId == wordId);

        var wordFormsTask = WordFormHelper.LoadWordFormsForWord(ctx1, wordId);

        var formFreqsTask = ctx2.WordFormFrequencies
            .AsNoTracking()
            .Where(wff => wff.WordId == wordId)
            .ToDictionaryAsync(wff => wff.ReadingIndex);

        var composedOfTask = CompositionHelper.LoadComposedOf(contextFactory, wordId, readingIndex);
        var usedInTask = CompositionHelper.LoadUsedIn(contextFactory, wordId, readingIndex, 0, 20);

        await Task.WhenAll(wordTask, wordFormsTask, formFreqsTask, composedOfTask, usedInTask);

        var word = await wordTask;
        if (word == null)
            return Results.NotFound();

        var xrefs = await ctx1.JmDictCrossReferences.AsNoTracking()
                              .Where(x => x.FromWordId == wordId)
                              .ToListAsync();

        var wordForms = await wordFormsTask;
        var mainForm = wordForms.FirstOrDefault(wf => wf.ReadingIndex == readingIndex);
        if (mainForm == null)
            return Results.NotFound();

        var formFreqs = await formFreqsTask;

        var mainFreq = formFreqs.GetValueOrDefault(mainForm.ReadingIndex);
        var mainReading = WordFormHelper.ToFormDto(mainForm, mainFreq);

        var (derivedFrom, derives) =
            await DerivationDisplayHelper.Load(contextFactory, derivationCache, wordId, readingIndex);

        List<WordFormDto> alternativeReadings = wordForms
                                                   .Select(form =>
                                                   {
                                                       var freq = formFreqs.GetValueOrDefault(form.ReadingIndex);
                                                       return WordFormHelper.ToPlainFormDto(form, freq);
                                                   })
                                                   .ToList();

        return Results.Ok(new WordDto
                          {
                              WordId = word.WordId, MainReading = mainReading, AlternativeReadings = alternativeReadings,
                              Definitions = word.Definitions.ToDefinitionDtos(xrefs.ToXrefsBySense()), PartsOfSpeech = word.PartsOfSpeech,
                              PitchAccents = word.PitchAccents, ComposedOf = await composedOfTask,
                              UsedIn = usedInTask.Result.Items,
                              UsedInTotal = usedInTask.Result.Total,
                              LanguageSources = word.LanguageSources.ToDto(),
                              EntryInfo = word.EntryInfo.Count > 0 ? word.EntryInfo.Select(e => e.Text).ToList() : null,
                              DerivedFrom = derivedFrom.Count > 0 ? derivedFrom : null,
                              Derives = derives.Count > 0 ? derives : null
                          });
    }

    [HttpGet("{wordId}/{readingIndex}/used-in")]
    [SwaggerOperation(Summary = "Get words that contain this word as a component",
                      Description = "Full list of compound words whose composition includes this (wordId, readingIndex) as a component, ordered by frequency.")]
    [ProducesResponseType(typeof(UsedInPageDto), StatusCodes.Status200OK)]
    [ResponseCache(Duration = 3600)]
    public async Task<UsedInPageDto> GetWordUsedIn([FromRoute] int wordId, [FromRoute] short readingIndex)
    {
        var (items, total) = await CompositionHelper.LoadUsedIn(contextFactory, wordId, readingIndex, 0, int.MaxValue);
        return new UsedInPageDto { Items = items, Total = total, Page = 1, PageSize = total };
    }

    [HttpGet("{wordId}/{readingIndex}/media-frequency")]
    [SwaggerOperation(Summary = "Get media frequency breakdown", Description = "Returns how many media of each type contain this word. Publicly cached.")]
    [ProducesResponseType(typeof(Dictionary<int, int>), StatusCodes.Status200OK)]
    [ResponseCache(Duration = 3600)]
    public async Task<Dictionary<int, int>> GetWordMediaFrequency([FromRoute] int wordId, [FromRoute] byte readingIndex)
    {
        return await context.DeckWords.AsNoTracking()
                                      .Where(dw => dw.WordId == wordId && dw.ReadingIndex == readingIndex)
                                      .Join(
                                            context.Decks.AsNoTracking()
                                                   .Where(d => d.ParentDeckId == null)
                                                   .Select(d => new { d.DeckId, d.MediaType }),
                                            dw => dw.DeckId,
                                            d => d.DeckId,
                                            (dw, d) => d.MediaType
                                           )
                                      .GroupBy(mediaType => mediaType)
                                      .Select(g => new { MediaType = g.Key, Count = g.Count() })
                                      .ToDictionaryAsync(x => (int)x.MediaType, x => x.Count);
    }

    /// <summary>
    /// Every ranking this form has: the site-wide one, one per media type that has observed it, the caller's saved
    /// custom lists, and which of them the caller's default resolves to.
    /// </summary>
    /// <remarks>Deliberately uncached: the resolved rank and the list ranks are per-user, and the word payload
    /// endpoints they overlay are publicly cached.</remarks>
    [HttpGet("{wordId}/{readingIndex}/frequency-ranks")]
    [SwaggerOperation(Summary = "Get every frequency ranking for a word form")]
    [ProducesResponseType(typeof(WordFrequencyRanksDto), StatusCodes.Status200OK)]
    public async Task<IResult> GetWordFrequencyRanks([FromRoute] int wordId, [FromRoute] byte readingIndex,
                                                     [FromQuery] bool includeLists = false)
    {
        var global = await context.WordFormFrequencies.AsNoTracking()
                                  .Where(f => f.WordId == wordId && f.ReadingIndex == readingIndex)
                                  .FirstOrDefaultAsync();

        var byTypeRows = await context.WordFormFrequenciesByType.AsNoTracking()
                                      .Where(f => f.WordId == wordId && f.ReadingIndex == readingIndex)
                                      .ToListAsync();

        var dto = new WordFrequencyRanksDto
        {
            Global = new FrequencyRankEntryDto
            {
                Rank = global?.FrequencyRank ?? 0,
                Percentage = global?.FrequencyPercentage ?? 0,
                Amount = global?.UsedInMediaAmount ?? 0
            },
            ByType = byTypeRows.ToDictionary(f => (int)f.MediaType, f => new FrequencyRankEntryDto
            {
                Rank = f.FrequencyRank, Percentage = f.FrequencyPercentage, Amount = f.UsedInMediaAmount
            })
        };

        var userId = currentUserService.UserId;
        if (userId == null)
        {
            dto.Resolved = new ResolvedFrequencyRankDto { Rank = dto.Global.Rank };
            return Results.Ok(dto);
        }

        var wordKey = WordFormHelper.EncodeWordKey(wordId, readingIndex);

        if (includeLists)
        {
            var saved = await userContext.UserFrequencyLists.AsNoTracking()
                                         .Where(f => f.UserId == userId && f.IsSaved && f.RankedWordsBlob != null)
                                         .OrderBy(f => f.Name)
                                         .Select(f => new { f.Id, f.Name })
                                         .ToListAsync();

            dto.Lists = [];
            foreach (var list in saved)
            {
                var ranks = await frequencySource.ListRanks(list.Id);
                ranks.TryGetValue(wordKey, out var listRank);
                dto.Lists.Add(new FrequencyListRankDto { Id = list.Id, Name = list.Name, Rank = listRank });
            }
        }

        var scope = await frequencySource.Resolve(userId);

        if (scope.MediaType is { } mediaType)
        {
            var typed = dto.ByType.GetValueOrDefault((int)mediaType);
            dto.Resolved = typed != null
                ? new ResolvedFrequencyRankDto
                {
                    Source = FrequencyRankSources.MediaType, MediaType = (int)mediaType, Rank = typed.Rank
                }
                : new ResolvedFrequencyRankDto
                {
                    Source = FrequencyRankSources.Global, MediaType = (int)mediaType, Rank = dto.Global.Rank, IsFallback = true
                };
        }
        else if (scope.FrequencyListId is { } listId)
        {
            var ranks = await frequencySource.ListRanks(listId);
            ranks.TryGetValue(wordKey, out var listRank);
            dto.Resolved = new ResolvedFrequencyRankDto
            {
                Source = FrequencyRankSources.List,
                ListId = listId,
                ListName = dto.Lists?.FirstOrDefault(l => l.Id == listId)?.Name
                           ?? await userContext.UserFrequencyLists.AsNoTracking()
                                               .Where(f => f.Id == listId).Select(f => f.Name).FirstOrDefaultAsync(),
                Rank = listRank
            };
        }
        else
        {
            dto.Resolved = new ResolvedFrequencyRankDto { Rank = dto.Global.Rank };
        }

        return Results.Ok(dto);
    }

    [HttpGet("{wordId}/{readingIndex}/known-state")]
    [SwaggerOperation(Summary = "Get user's known state for a word", Description = "Returns the current user's known/learning state for a specific word and reading.")]
    [ProducesResponseType(typeof(List<KnownState>), StatusCodes.Status200OK)]
    public async Task<List<KnownState>> GetWordKnownState([FromRoute] int wordId, [FromRoute] byte readingIndex)
    {
        return await currentUserService.GetKnownWordState(wordId, readingIndex);
    }

    /// <summary>
    /// Which known entry makes this form redundant through a derivation the user has enabled.
    /// </summary>
    /// <remarks>Kept off the publicly-cached word payload because the answer is per-user.</remarks>
    [HttpGet("{wordId}/{readingIndex}/derivation-cover")]
    [SwaggerOperation(Summary = "Get the derivation covering a word",
                      Description = "Returns the known family member that makes this form redundant, or null.")]
    [ProducesResponseType(typeof(DerivationCoverDto), StatusCodes.Status200OK)]
    public async Task<IResult> GetDerivationCover([FromRoute] int wordId, [FromRoute] byte readingIndex)
    {
        var cover = await currentUserService.GetCoveringDerivation(wordId, readingIndex);
        return Results.Ok(await DerivationDisplayHelper.LoadCover(contextFactory, cover));
    }

    /// <summary>
    /// Gets the kanji breakdown for a specific word reading.
    /// </summary>
    /// <param name="wordId"></param>
    /// <param name="readingIndex"></param>
    /// <returns>List of kanji in the word with their metadata.</returns>
    [HttpGet("{wordId}/{readingIndex}/kanji")]
    [SwaggerOperation(Summary = "Get kanji breakdown for word", Description = "Returns the kanji characters in a word reading with their metadata (stroke count, JLPT, meanings, frequency).")]
    [ProducesResponseType(typeof(List<KanjiListDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IResult> GetWordKanji([FromRoute] int wordId, [FromRoute] short readingIndex)
    {
        var wordKanjis = await context.WordKanjis
            .AsNoTracking()
            .Where(wk => wk.WordId == wordId && wk.ReadingIndex == readingIndex)
            .OrderBy(wk => wk.Position)
            .Select(wk => wk.KanjiCharacter)
            .ToListAsync();

        if (wordKanjis.Count == 0)
            return Results.Ok(new List<KanjiListDto>());
        

        var kanjis = await context.Kanjis
            .AsNoTracking()
            .Where(k => wordKanjis.Contains(k.Character))
            .ToDictionaryAsync(k => k.Character);

        // Preserve order based on position in word
        var result = wordKanjis
            .Where(c => kanjis.ContainsKey(c))
            .Select(c => kanjis[c])
            .Select(k => new KanjiListDto
            {
                Character = k.Character,
                Meanings = k.Meanings,
                StrokeCount = k.StrokeCount,
                JlptLevel = k.JlptLevel,
                Grade = k.Grade,
                FrequencyRank = k.FrequencyRank
            })
            .ToList();

        return Results.Ok(result);
    }

    /// <summary>
    /// Parses the provided text and returns a sequence of parsed and unparsed segments as deck words.
    /// </summary>
    /// <param name="text">Text to parse. Max length 2000 characters.</param>
    /// <returns>List of parsed and unparsed segments preserving original order.</returns>
    [HttpGet("parse")]
    [SwaggerOperation(Summary = "Parse text into words", Description = "Parses the provided text and returns parsed words and any gaps as separate items, preserving order.")]
    [ProducesResponseType(typeof(List<ParsedWordDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IResult> Parse([FromQuery] string text)
    {
        if (text.Length > 2000)
            return Results.BadRequest("Text is too long");

        var (cleanText, _) = FuriganaHintExtractor.Extract(text);
        var parsedWords = await Parser.Parser.ParseText(contextFactory, text);

        var allWords = new List<ParsedWordDto>();
        var wordsWithPositions = new List<(ParsedWordDto Word, int Position)>();
        int currentPosition = 0;

        BuildParseResult(cleanText, parsedWords, wordsWithPositions, allWords);
        return Results.Ok(allWords);
    }

    /// <summary>
    /// Normalises and parses the provided text. Converts romaji to hiragana,
    /// halfwidth digits/letters to fullwidth, then parses and returns words.
    /// </summary>
    /// <param name="text">Text to normalise and parse. Max length 2000 characters.</param>
    /// <returns>Normalised text and list of parsed/unparsed segments.</returns>
    [HttpGet("parse-normalised")]
    [SwaggerOperation(Summary = "Normalise and parse text into words",
                      Description = "Normalises input (romaji→hiragana, halfwidth→fullwidth) then parses into words.")]
    [ProducesResponseType(typeof(ParseNormalisedResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IResult> ParseNormalised([FromQuery] string text)
    {
        if (text.Length > 2000)
            return Results.BadRequest("Text is too long");

        var (cleanTextRaw, hints) = FuriganaHintExtractor.Extract(text);
        var normalisedClean = TextNormalizationHelper.NormaliseForParsing(cleanTextRaw);
        var textForParser = hints.Length > 0
            ? FuriganaHintExtractor.Annotate(normalisedClean, hints)
            : normalisedClean;
        var parsedWords = await Parser.Parser.ParseText(contextFactory, textForParser);

        var allWords = new List<ParsedWordDto>();
        var wordsWithPositions = new List<(ParsedWordDto Word, int Position)>();
        int currentPosition = 0;

        BuildParseResult(normalisedClean, parsedWords, wordsWithPositions, allWords);
        return Results.Ok(new ParseNormalisedResultDto { NormalisedText = normalisedClean, Words = allWords });
    }

    /// <summary>
    /// Gets IDs of words whose media frequency rank falls within the specified inclusive range.
    /// </summary>
    /// <param name="minFrequency">Minimum frequency rank (inclusive).</param>
    /// <param name="maxFrequency">Maximum frequency rank (inclusive).</param>
    /// <returns>List of word IDs.</returns>
    [HttpGet("vocabulary-list-frequency/{minFrequency}/{maxFrequency}")]
    [SwaggerOperation(Summary = "Get vocabulary IDs by media frequency range")]
    [ProducesResponseType(typeof(List<int>), StatusCodes.Status200OK)]
    public IResult GetVocabularyByMediaFrequencyRange([FromRoute] int minFrequency, [FromRoute] int maxFrequency)
    {
        var query = context.JmDictWordFrequencies.Where(f => f.FrequencyRank >= minFrequency && f.FrequencyRank <= maxFrequency);

        return Results.Ok(query.Select(f => f.WordId).ToList());
    }

    /// <summary>
    /// Returns up to three random example sentences for the given word and reading index, excluding already loaded ones.
    /// </summary>
    /// <param name="wordId">The word ID.</param>
    /// <param name="readingIndex">The reading index for the word.</param>
    /// <param name="alreadyLoaded">A list of deck IDs already loaded on the client to avoid duplicates.</param>
    /// <param name="mediaType">Optional media type filter.</param>
    /// <returns>A list of example sentences with metadata.</returns>
    [HttpPost("{wordId}/{readingIndex}/random-example-sentences/{mediaType?}")]
    [EnableRateLimiting("heavy")]
    [SwaggerOperation(Summary = "Get random example sentences",
                      Description =
                          "Returns up to three random example sentences for the given word and reading index, excluding already loaded ones.")]
    [ProducesResponseType(typeof(List<ExampleSentenceDto>), StatusCodes.Status200OK)]
    public Task<List<ExampleSentenceDto>> GetRandomExampleSentences([FromRoute] int wordId, [FromRoute] int readingIndex,
                                                                    [FromBody] List<int> alreadyLoaded, [FromRoute] MediaType? mediaType = null)
    {
        return exampleSentences.GetRandomAsync(wordId, readingIndex, alreadyLoaded, mediaType, 3);
    }

    [HttpPost("{wordId}/{readingIndex}/example-sentences-by-difficulty/{mediaType?}")]
    [EnableRateLimiting("heavy")]
    [SwaggerOperation(Summary = "Get example sentences ordered by difficulty",
                      Description =
                          "Returns example sentences for the given word and reading index, ordered by difficulty score. " +
                          "Automatically expands the band (ascending or descending) until `take` sentences are found or the range is exhausted.")]
    [ProducesResponseType(typeof(ExampleSentencesByDifficultyResponse), StatusCodes.Status200OK)]
    public Task<ExampleSentencesByDifficultyResponse> GetExampleSentencesByDifficulty(
        [FromRoute] int wordId, [FromRoute] int readingIndex,
        [FromBody] List<int> alreadyLoaded, [FromRoute] MediaType? mediaType = null,
        [FromQuery] float minDifficulty = 0f, [FromQuery] float maxDifficulty = 0.5f,
        [FromQuery] bool descending = false, [FromQuery] int take = 3)
    {
        return exampleSentences.GetByDifficultyAsync(wordId, readingIndex, alreadyLoaded, mediaType,
                                                     minDifficulty, maxDifficulty, descending, take);
    }


    /// <summary>
    /// Searches the dictionary by Japanese text, romaji, English meaning, or wildcard pattern.
    /// </summary>
    [HttpGet("search")]
    [SwaggerOperation(Summary = "Search dictionary",
                      Description = "Searches by Japanese text, romaji, English meaning, or wildcard pattern (use * for wildcard).")]
    [ProducesResponseType(typeof(DictionarySearchResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IResult> SearchDictionary(
        [FromQuery] string query,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Results.Ok(new DictionarySearchResultDto { Query = query ?? "" });

        limit = Math.Clamp(limit, 1, 100);
        var originalTrimmed = query.Trim();
        var trimmed = originalTrimmed.ToLowerInvariant();

        if (trimmed.Length > 200)
            return Results.BadRequest("Query too long");

        var cacheKey = $"jiten:dict-search:v2:{trimmed}:{limit}:{offset}";
        var redisDb = redis.GetDatabase();
        try
        {
            var cachedJson = await redisDb.StringGetAsync(cacheKey);
            if (cachedJson.HasValue)
            {
                var cachedDto = JsonSerializer.Deserialize<DictionarySearchResultDto>(cachedJson!);
                if (cachedDto != null)
                {
                    await ApplyScopedRanks(cachedDto);
                    return Results.Ok(cachedDto);
                }
            }
        }
        catch { }

        var hasWildcard = trimmed.Contains('*');
        var cleanText = trimmed.Replace("*", "");

        if (cleanText.Length == 0)
            return Results.BadRequest("Query must contain searchable text");

        var hasJapanese = ContainsJapanese(cleanText);
        var hasFullwidthLatin = cleanText.Any(c => (c >= '\uFF21' && c <= '\uFF3A') || (c >= '\uFF41' && c <= '\uFF5A'));
        var isAsciiOnly = cleanText.All(c => c < 128);
        var hasSpaces = cleanText.Contains(' ');

        string queryType;
        List<DictionaryEntryDto> results;
        List<DictionaryEntryDto> dictionaryResults = [];

        if (hasWildcard)
        {
            queryType = "wildcard";
            var parts = trimmed.Split('*');

            string[] processedParts;
            if (!hasJapanese && isAsciiOnly && !hasSpaces)
            {
                processedParts = parts.Select(p =>
                    string.IsNullOrEmpty(p) ? "" : SanitizeLikeInput(WanaKana.ToHiragana(p.ToLowerInvariant()))
                ).ToArray();
            }
            else
            {
                processedParts = parts.Select(SanitizeLikeInput).ToArray();
            }

            var likePattern = string.Join("%", processedParts);
            results = await SearchLookupsByPattern(likePattern, limit, offset);
        }
        else if (hasJapanese)
        {
            queryType = "japanese";
            var wordIds = await SearchLookupsExact(trimmed, limit, offset);
            results = await BuildDictionaryEntries(wordIds, trimmed);
        }
        else if (hasFullwidthLatin)
        {
            queryType = "japanese";
            var wordIds = await SearchLookupsExact(originalTrimmed, limit, offset);
            results = await BuildDictionaryEntries(wordIds, originalTrimmed);
        }
        else if (isAsciiOnly && !hasSpaces)
        {
            var hiragana = WanaKana.ToHiragana(trimmed.ToLowerInvariant());
            var isValidRomaji = hiragana.All(c =>
                (c >= '\u3040' && c <= '\u309F') ||
                (c >= '\u30A0' && c <= '\u30FF'));

            if (isValidRomaji)
            {
                var romajiWordIds = await SearchLookupsExact(hiragana, limit, offset);
                if (romajiWordIds.Count > 0)
                {
                    queryType = "romaji";
                    results = await BuildDictionaryEntries(romajiWordIds, preferMostCommonForm: true);

                    var englishResults = await SearchByEnglishGloss(trimmed, limit, offset);
                    var existingWordIds = results.Select(r => r.WordId).ToHashSet();
                    dictionaryResults = englishResults.Where(e => !existingWordIds.Contains(e.WordId)).ToList();
                }
                else
                {
                    queryType = "english";
                    results = await SearchByEnglishGloss(trimmed, limit, offset);
                }
            }
            else
            {
                queryType = "english";
                results = await SearchByEnglishGloss(trimmed, limit, offset);
            }
        }
        else
        {
            queryType = "english";
            results = await SearchByEnglishGloss(trimmed, limit, offset);
        }

        var dto = new DictionarySearchResultDto
        {
            Query = trimmed,
            QueryType = queryType,
            Results = results,
            DictionaryResults = dictionaryResults,
            HasMore = results.Count >= limit
        };

        try { await redisDb.StringSetAsync(cacheKey, JsonSerializer.Serialize(dto), TimeSpan.FromDays(7)); }
        catch { }

        await ApplyScopedRanks(dto);
        return Results.Ok(dto);
    }

    /// <summary>Runs after the search payload is cached in Redis: the cache is shared, the caller's ranking is not.</summary>
    private async Task ApplyScopedRanks(DictionarySearchResultDto dto)
    {
        var scope = await frequencySource.Resolve();
        if (scope.IsGlobal) return;

        var entries = dto.Results.Concat(dto.DictionaryResults).ToList();
        var wordIds = entries.Select(e => e.WordId).Distinct().ToList();
        if (wordIds.Count == 0) return;

        var scoped = await frequencySource.LoadFrequencies(context, wordIds, scope);
        foreach (var entry in entries)
        {
            var resolved = scoped.Resolve(entry.WordId, entry.ReadingIndex);
            entry.FrequencyRank = resolved.Rank == 0 ? int.MaxValue : resolved.Rank;
            entry.FrequencyRankSource = resolved.Source;
            entry.IsFrequencyFallback = resolved.IsFallback ? true : null;
        }
    }

    #region Dictionary search helpers

    private async Task<List<int>> SearchLookupsExact(string lookupKey, int limit, int offset)
    {
        var hiragana = JapaneseTextHelper.ToHiragana(lookupKey);
        return await context.Lookups
            .AsNoTracking()
            .Where(l => l.LookupKey == lookupKey || (hiragana != lookupKey && l.LookupKey == hiragana))
            .Select(l => l.WordId)
            .Distinct()
            .OrderBy(id => id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();
    }

    private async Task<List<DictionaryEntryDto>> SearchLookupsByPattern(string likePattern, int limit, int offset)
    {
        var wordIds = await context.Lookups
            .AsNoTracking()
            .Where(l => EF.Functions.Like(l.LookupKey, likePattern))
            .Select(l => l.WordId)
            .Distinct()
            .OrderBy(id => id)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();

        return await BuildDictionaryEntries(wordIds);
    }

    private async Task<List<DictionaryEntryDto>> SearchByEnglishGloss(string query, int limit, int offset)
    {
        var candidateLimit = Math.Max((limit + offset) * 6, 300);

        var candidates = await FetchGlossCandidates(query, candidateLimit, GlossMatchMode.AllTerms);
        if (candidates.Count == 0)
            candidates = await FetchGlossCandidates(query, candidateLimit, GlossMatchMode.AnyTerm);
        if (candidates.Count == 0)
            candidates = await FetchGlossCandidates(query, candidateLimit, GlossMatchMode.Phrase);
        if (candidates.Count == 0) return [];

        var frequencyRanks = candidates
            .Where(c => c.FrequencyRank.HasValue)
            .GroupBy(c => c.WordId)
            .ToDictionary(g => g.Key, g => g.Min(c => c.FrequencyRank!.Value));

        var ranked = GlossSearchScorer.Rank(
            query,
            candidates.Select(c => new GlossSenseCandidate(c.WordId, c.SenseIndex, c.EnglishMeanings, c.Misc, c.GlossTypes, c.IsCommon)),
            frequencyRanks);

        var page = ranked.Skip(offset).Take(limit).ToList();
        var entries = await BuildDictionaryEntries(page.Select(h => h.WordId).ToList());
        var byWord = entries.ToDictionary(e => e.WordId);

        var result = new List<DictionaryEntryDto>(page.Count);
        foreach (var hit in page)
        {
            if (!byWord.TryGetValue(hit.WordId, out var entry)) continue;
            entry.Meanings = hit.Meanings;
            result.Add(entry);
        }
        return result;
    }

    private sealed class GlossCandidateRow
    {
        public int WordId { get; set; }
        public int SenseIndex { get; set; }
        public List<string> EnglishMeanings { get; set; } = [];
        public List<string> Misc { get; set; } = [];
        public List<string> GlossTypes { get; set; } = [];
        public int? FrequencyRank { get; set; }
        public bool IsCommon { get; set; }
    }

    private enum GlossMatchMode { AllTerms, AnyTerm, Phrase }

    /// <summary>AllTerms and AnyTerm hit the gloss tsvector index; Phrase is the unindexed whole-phrase scan kept for stopword-only queries like "with".</summary>
    private async Task<List<GlossCandidateRow>> FetchGlossCandidates(string query, int candidateLimit, GlossMatchMode mode)
    {
        const string columns = """
            SELECT d."WordId", d."SenseIndex", d."EnglishMeanings", d."Misc", d."GlossTypes",
                   (SELECT MIN(f."FrequencyRank") FROM jmdict."WordFrequencies" f WHERE f."WordId" = d."WordId") AS "FrequencyRank",
                   EXISTS (SELECT 1 FROM jmdict."WordForms" wf WHERE wf."WordId" = d."WordId"
                           AND wf."Priorities" && ARRAY['ichi1','news1','spec1','gai1']) AS "IsCommon"
            FROM jmdict."Definitions" d
            """;

        if (mode == GlossMatchMode.Phrase)
        {
            return await context.Database
                .SqlQueryRaw<GlossCandidateRow>(
                    columns + """

                    WHERE EXISTS (SELECT 1 FROM unnest(d."EnglishMeanings") AS m WHERE m ~* {0})
                    ORDER BY "FrequencyRank" ASC NULLS LAST
                    LIMIT {1}
                    """, $@"\m{SanitizeRegexInput(query)}\M", candidateLimit)
                .ToListAsync();
        }

        var tsquery = mode == GlossMatchMode.AnyTerm
            ? "replace(plainto_tsquery('english', {0})::text, '&', '|')::tsquery"
            : "plainto_tsquery('english', {0})";

        return await context.Database
            .SqlQueryRaw<GlossCandidateRow>(
                columns + $$"""

                WHERE numnode({{tsquery}}) > 0 AND d."SearchVector" @@ {{tsquery}}
                ORDER BY ts_rank_cd(d."SearchVector", {{tsquery}}, 1) DESC, "FrequencyRank" ASC NULLS LAST
                LIMIT {1}
                """, query, candidateLimit)
            .ToListAsync();
    }

    private async Task<List<DictionaryEntryDto>> BuildDictionaryEntries(List<int> wordIds, string? matchText = null, bool preferMostCommonForm = false)
    {
        if (wordIds.Count == 0) return [];

        var words = await context.JMDictWords
            .AsNoTracking()
            .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
            .Where(w => wordIds.Contains(w.WordId))
            .ToListAsync();

        var wordForms = await context.WordForms
            .AsNoTracking()
            .Where(wf => wordIds.Contains(wf.WordId))
            .ToListAsync();
        RubyTextHelper.EnrichForms(wordForms);

        var formsByWord = wordForms.GroupBy(f => f.WordId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var frequencies = await context.WordFormFrequencies
            .AsNoTracking()
            .Where(f => wordIds.Contains(f.WordId))
            .ToListAsync();

        var freqByWordReading = frequencies
            .ToDictionary(f => (f.WordId, f.ReadingIndex));

        return words
            .Select(w =>
            {
                if (!formsByWord.TryGetValue(w.WordId, out var forms) || forms.Count == 0)
                    return null;

                JmDictWordForm? bestForm = null;
                if (preferMostCommonForm)
                {
                    bestForm = forms
                        .Where(f => !f.IsSearchOnly)
                        .OrderByDescending(f => freqByWordReading.ContainsKey((w.WordId, f.ReadingIndex)) ? 1 : 0)
                        .ThenBy(f => freqByWordReading.GetValueOrDefault((w.WordId, f.ReadingIndex))?.FrequencyRank ?? int.MaxValue)
                        .ThenBy(f => f.ReadingIndex)
                        .FirstOrDefault();
                }
                else if (matchText != null)
                {
                    bestForm = forms.FirstOrDefault(f => f.Text == matchText);
                }
                bestForm ??= forms.OrderBy(f => f.ReadingIndex).First();

                var freq = freqByWordReading.GetValueOrDefault((w.WordId, bestForm.ReadingIndex));
                var firstDef = w.Definitions
                    .Where(d => d.EnglishMeanings.Count > 0)
                    .OrderBy(d => d.SenseIndex)
                    .FirstOrDefault();

                string? primaryKanjiText = null;
                if (bestForm.FormType == JmDictFormType.KanaForm)
                {
                    var kanjiForm = forms
                        .Where(f => f.FormType == JmDictFormType.KanjiForm && !f.IsSearchOnly)
                        .OrderByDescending(f => freqByWordReading.GetValueOrDefault((w.WordId, f.ReadingIndex))?.FrequencyRank != null ? 1 : 0)
                        .ThenBy(f => freqByWordReading.GetValueOrDefault((w.WordId, f.ReadingIndex))?.FrequencyRank ?? int.MaxValue)
                        .ThenBy(f => f.ReadingIndex)
                        .FirstOrDefault();
                    if (kanjiForm != null)
                        primaryKanjiText = kanjiForm.RubyText;
                }

                return new DictionaryEntryDto
                {
                    WordId = w.WordId,
                    ReadingIndex = (byte)bestForm.ReadingIndex,
                    Text = bestForm.Text,
                    RubyText = bestForm.RubyText,
                    PrimaryKanjiText = primaryKanjiText,
                    PartsOfSpeech = w.PartsOfSpeech,
                    Meanings = firstDef?.EnglishMeanings ?? [],
                    FrequencyRank = freq?.FrequencyRank ?? int.MaxValue
                };
            })
            .Where(e => e != null)
            .OrderBy(e => e!.FrequencyRank)
            .Cast<DictionaryEntryDto>()
            .ToList();
    }

    private static bool ContainsJapanese(string text) => SearchHelper.ContainsJapanese(text);

    private static string SanitizeLikeInput(string input) => SearchHelper.SanitizeLikeInput(input);

    private static string SanitizeRegexInput(string input)
    {
        return System.Text.RegularExpressions.Regex.Replace(input, @"[.*+?^${}()|[\]\\]", @"\$&");
    }

    #endregion

    private static void BuildParseResult(
        string sourceText,
        List<DeckWord> parsedWords,
        List<(ParsedWordDto Word, int Position)> wordsWithPositions,
        List<ParsedWordDto> allWords)
    {
        int currentPosition = 0;

        foreach (var word in parsedWords)
        {
            var (position, sourceLength) = TokenPositionHelper.FindTokenInSource(sourceText, word.OriginalText, currentPosition);
            if (position >= 0)
            {
                var dto = new ParsedWordDto(word);
                if (sourceLength != word.OriginalText.Length)
                    dto.OriginalText = sourceText.Substring(position, sourceLength);
                wordsWithPositions.Add((dto, position));
                currentPosition = position + sourceLength;
            }
        }

        currentPosition = 0;
        foreach (var (word, position) in wordsWithPositions)
        {
            if (position > currentPosition)
            {
                string gap = sourceText.Substring(currentPosition, position - currentPosition);
                allWords.Add(new ParsedWordDto(gap));
            }

            allWords.Add(word);
            currentPosition = position + word.OriginalText.Length;
        }

        if (currentPosition < sourceText.Length)
        {
            string gap = sourceText.Substring(currentPosition);
            allWords.Add(new ParsedWordDto(gap));
        }
    }
}