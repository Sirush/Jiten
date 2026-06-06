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
public class VocabularyController(JitenDbContext context, IDbContextFactory<JitenDbContext> contextFactory, ICurrentUserService currentUserService, IConnectionMultiplexer redis) : ControllerBase
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

        var word = await wordTask;
        if (word == null)
            return Results.NotFound();

        var wordForms = await wordFormsTask;
        var mainForm = wordForms.FirstOrDefault(wf => wf.ReadingIndex == readingIndex);
        if (mainForm == null)
            return Results.NotFound();

        var formFreqs = await formFreqsTask;
        var usedInMediaByType = await usedInMediaByTypeTask;

        var mainFreq = formFreqs.GetValueOrDefault(mainForm.ReadingIndex);
        var mainReading = WordFormHelper.ToFormDto(mainForm, mainFreq, usedInMediaByType);

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
                              Definitions = word.Definitions.ToDefinitionDtos(), PartsOfSpeech = word.PartsOfSpeech,
                              PitchAccents = word.PitchAccents, KnownStates = await knownStatesTask,
                              ComposedOf = await composedOfTask,
                              UsedIn = usedInTask.Result.Items,
                              UsedInTotal = usedInTask.Result.Total
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

        var wordForms = await wordFormsTask;
        var mainForm = wordForms.FirstOrDefault(wf => wf.ReadingIndex == readingIndex);
        if (mainForm == null)
            return Results.NotFound();

        var formFreqs = await formFreqsTask;

        var mainFreq = formFreqs.GetValueOrDefault(mainForm.ReadingIndex);
        var mainReading = WordFormHelper.ToFormDto(mainForm, mainFreq);

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
                              Definitions = word.Definitions.ToDefinitionDtos(), PartsOfSpeech = word.PartsOfSpeech,
                              PitchAccents = word.PitchAccents, ComposedOf = await composedOfTask,
                              UsedIn = usedInTask.Result.Items,
                              UsedInTotal = usedInTask.Result.Total
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

    [HttpGet("{wordId}/{readingIndex}/known-state")]
    [SwaggerOperation(Summary = "Get user's known state for a word", Description = "Returns the current user's known/learning state for a specific word and reading.")]
    [ProducesResponseType(typeof(List<KnownState>), StatusCodes.Status200OK)]
    public async Task<List<KnownState>> GetWordKnownState([FromRoute] int wordId, [FromRoute] byte readingIndex)
    {
        return await currentUserService.GetKnownWordState(wordId, readingIndex);
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
    [SwaggerOperation(Summary = "Get random example sentences",
                      Description =
                          "Returns up to three random example sentences for the given word and reading index, excluding already loaded ones.")]
    [ProducesResponseType(typeof(List<ExampleSentenceDto>), StatusCodes.Status200OK)]
    public async Task<List<ExampleSentenceDto>> GetRandomExampleSentences([FromRoute] int wordId, [FromRoute] int readingIndex,
                                                                          [FromBody] List<int> alreadyLoaded, [FromRoute] MediaType? mediaType = null)
    {
        // Subquery: distinct sentence IDs for this word+reading (not materialised)
        var sentenceIdSubquery = context.ExampleSentenceWords
            .Where(w => w.WordId == wordId && w.ReadingIndex == readingIndex)
            .Select(w => w.ExampleSentenceId)
            .Distinct();

        // Filter, deduplicate, exclude already-loaded decks, sample 3 random — all in SQL
        var picked = await context.ExampleSentences
            .AsNoTracking()
            .Where(s => sentenceIdSubquery.Contains(s.SentenceId))
            .Join(context.Decks.AsNoTracking(),
                  s => s.DeckId, d => d.DeckId,
                  (s, d) => new { Sentence = s, Deck = d })
            .Where(j => !mediaType.HasValue || j.Deck.MediaType == mediaType.Value)
            .Where(j => !alreadyLoaded.Contains(j.Deck.DeckId)
                     && (!j.Deck.ParentDeckId.HasValue || !alreadyLoaded.Contains(j.Deck.ParentDeckId.Value)))
            .OrderBy(_ => EF.Functions.Random())
            .Take(3)
            .Select(j => new PickedSentence(
                j.Sentence.SentenceId, j.Sentence.Text, j.Sentence.Difficulty,
                j.Deck.DeckId, j.Deck.ParentDeckId))
            .ToListAsync();

        if (picked.Count == 0) return [];

        return await BuildExampleSentenceDtos(picked, wordId, readingIndex);
    }

    [HttpPost("{wordId}/{readingIndex}/example-sentences-by-difficulty/{mediaType?}")]
    [SwaggerOperation(Summary = "Get example sentences ordered by difficulty",
                      Description =
                          "Returns example sentences for the given word and reading index, ordered by difficulty score. " +
                          "Automatically expands the band (ascending or descending) until `take` sentences are found or the range is exhausted.")]
    [ProducesResponseType(typeof(ExampleSentencesByDifficultyResponse), StatusCodes.Status200OK)]
    public async Task<ExampleSentencesByDifficultyResponse> GetExampleSentencesByDifficulty(
        [FromRoute] int wordId, [FromRoute] int readingIndex,
        [FromBody] List<int> alreadyLoaded, [FromRoute] MediaType? mediaType = null,
        [FromQuery] float minDifficulty = 0f, [FromQuery] float maxDifficulty = 0.5f,
        [FromQuery] bool descending = false, [FromQuery] int take = 3)
    {
        take = Math.Clamp(take, 1, 20);
        const float bandSize = 0.5f;
        const int maxIterations = 40;

        var sentenceIdSubquery = context.ExampleSentenceWords
            .Where(w => w.WordId == wordId && w.ReadingIndex == readingIndex)
            .Select(w => w.ExampleSentenceId)
            .Distinct();

        var baseSentences = context.ExampleSentences
            .AsNoTracking()
            .Where(s => sentenceIdSubquery.Contains(s.SentenceId));

        var globalMin = await baseSentences.Select(s => (float?)s.Difficulty).MinAsync() ?? 0f;
        var globalMax = await baseSentences.Select(s => (float?)s.Difficulty).MaxAsync() ?? 0f;

        var collected = new List<PickedSentence>();
        var bandMin = minDifficulty;
        var bandMax = maxDifficulty;

        if (descending && bandMin > globalMax)
        {
            bandMax = (float)(Math.Ceiling(globalMax / bandSize) * bandSize + bandSize);
            bandMin = bandMax - bandSize;
        }

        for (var i = 0; i < maxIterations && collected.Count < take; i++)
        {
            if (descending ? bandMax < globalMin : bandMin > globalMax + bandSize)
                break;

            var remaining = take - collected.Count;
            var excludeIds = alreadyLoaded.Concat(collected.Select(c => c.DeckId)).Distinct().ToList();

            var batch = await baseSentences
                .Where(s => s.Difficulty >= bandMin && s.Difficulty < bandMax)
                .Join(context.Decks.AsNoTracking(),
                      s => s.DeckId, d => d.DeckId,
                      (s, d) => new { Sentence = s, Deck = d })
                .Where(j => !mediaType.HasValue || j.Deck.MediaType == mediaType.Value)
                .Where(j => !excludeIds.Contains(j.Deck.DeckId)
                         && (!j.Deck.ParentDeckId.HasValue || !excludeIds.Contains(j.Deck.ParentDeckId.Value)))
                .OrderBy(_ => EF.Functions.Random())
                .Take(remaining)
                .Select(j => new PickedSentence(
                    j.Sentence.SentenceId, j.Sentence.Text, j.Sentence.Difficulty,
                    j.Deck.DeckId, j.Deck.ParentDeckId))
                .ToListAsync();

            collected.AddRange(batch);

            if (descending)
            {
                bandMax = bandMin;
                bandMin -= bandSize;
            }
            else
            {
                bandMin = bandMax;
                bandMax += bandSize;
            }
        }

        return new ExampleSentencesByDifficultyResponse
        {
            MinDifficulty = globalMin,
            MaxDifficulty = globalMax,
            SearchedBandMin = minDifficulty,
            SearchedBandMax = descending ? bandMax + bandSize : bandMin,
            Sentences = collected.Count == 0 ? [] : await BuildExampleSentenceDtos(collected, wordId, readingIndex)
        };
    }

    private record PickedSentence(int SentenceId, string Text, float Difficulty, int DeckId, int? ParentDeckId);

    private async Task<List<ExampleSentenceDto>> BuildExampleSentenceDtos(
        List<PickedSentence> picked, int wordId, int readingIndex)
    {
        var selectedIds = picked.Select(p => p.SentenceId).ToList();
        var positionMap = (await context.ExampleSentenceWords
            .AsNoTracking()
            .Where(w => w.WordId == wordId && w.ReadingIndex == readingIndex
                        && selectedIds.Contains(w.ExampleSentenceId))
            .Select(w => new { w.ExampleSentenceId, w.Position, w.Length })
            .ToListAsync())
            .DistinctBy(w => w.ExampleSentenceId)
            .ToDictionary(w => w.ExampleSentenceId);

        var childDeckIds = picked.Select(p => p.DeckId).Distinct().ToList();
        var childDecks = await context.Decks.AsNoTracking()
            .Where(d => childDeckIds.Contains(d.DeckId))
            .ToDictionaryAsync(d => d.DeckId, d => d);

        var parentIds = picked.Where(p => p.ParentDeckId.HasValue).Select(p => p.ParentDeckId!.Value).Distinct().ToList();
        var parentDecks = parentIds.Count > 0
            ? await context.Decks.AsNoTracking()
                .Where(d => parentIds.Contains(d.DeckId))
                .ToDictionaryAsync(d => d.DeckId, d => d)
            : new Dictionary<int, Deck>();

        return picked.Select(p =>
        {
            positionMap.TryGetValue(p.SentenceId, out var pos);
            childDecks.TryGetValue(p.DeckId, out var sourceDeck);
            Deck? parentDeck = null;
            if (p.ParentDeckId.HasValue)
                parentDecks.TryGetValue(p.ParentDeckId.Value, out parentDeck);

            return new ExampleSentenceDto
            {
                SentenceId = p.SentenceId,
                Text = p.Text,
                Difficulty = p.Difficulty,
                WordPosition = pos?.Position ?? 0,
                WordLength = pos?.Length ?? 0,
                SourceDeck = sourceDeck!,
                SourceDeckParent = parentDeck
            };
        }).ToList();
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

        var cacheKey = $"jiten:dict-search:{trimmed}:{limit}:{offset}";
        var redisDb = redis.GetDatabase();
        try
        {
            var cachedJson = await redisDb.StringGetAsync(cacheKey);
            if (cachedJson.HasValue)
                return Results.Ok(JsonSerializer.Deserialize<DictionarySearchResultDto>(cachedJson!));
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

        return Results.Ok(dto);
    }

    #region Dictionary search helpers

    private async Task<List<int>> SearchLookupsExact(string lookupKey, int limit, int offset)
    {
        var hiragana = WanaKana.ToHiragana(lookupKey);
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
        var regexPattern = $@"\m{SanitizeRegexInput(query)}\M";
        var oversampleLimit = Math.Max(limit * 4, 100);

        var wordIds = await context.Database
            .SqlQueryRaw<int>(
                """
                SELECT w."WordId" AS "Value"
                FROM (
                    SELECT DISTINCT d."WordId", f."FrequencyRank"
                    FROM jmdict."Definitions" d
                    LEFT JOIN jmdict."WordFrequencies" f ON d."WordId" = f."WordId"
                    WHERE EXISTS (
                        SELECT 1 FROM unnest(d."EnglishMeanings") AS m
                        WHERE m ~* {0}
                    )
                ) w
                ORDER BY w."FrequencyRank" ASC NULLS LAST
                LIMIT {1}
                """, regexPattern, oversampleLimit)
            .ToListAsync();

        var entries = await BuildDictionaryEntries(wordIds, englishQuery: query);
        return entries
            .OrderByDescending(e => e.Meanings.Any(m => m.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(e => e.FrequencyRank)
            .Skip(offset)
            .Take(limit)
            .ToList();
    }

    private async Task<List<DictionaryEntryDto>> BuildDictionaryEntries(List<int> wordIds, string? matchText = null, string? englishQuery = null, bool preferMostCommonForm = false)
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

                if (englishQuery != null)
                {
                    var matchingDef = w.Definitions
                        .Where(d => d.EnglishMeanings.Any(m => m.Contains(englishQuery, StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(d => d.SenseIndex)
                        .FirstOrDefault();
                    if (matchingDef != null) firstDef = matchingDef;
                }

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