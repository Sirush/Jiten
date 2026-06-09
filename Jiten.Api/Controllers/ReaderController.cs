using Jiten.Api.Dtos;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Helpers;
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
[Route("api/reader")]
[Authorize]
public class ReaderController(
    JitenDbContext context,
    IDbContextFactory<JitenDbContext> contextFactory,
    ICurrentUserService currentUserService,
    IParseThrottleService parseThrottle,
    IStudyDeckMembershipService deckMembership,
    ILogger<ReaderController> logger) : ControllerBase
{
    [HttpPost("ping")]
    public IResult Ping()
    {
        return Results.Ok(new { success = true });
    }

    /// <summary>
    /// Parses the provided text and returns a sequence of parsed and unparsed segments as deck words.
    /// </summary>
    /// <param name="request">Request containing text to parse.</param>
    /// <returns>List of parsed and unparsed segments preserving original order.</returns>
    [HttpPost("parse")]
    [SwaggerOperation(Summary = "Parse text into words",
                      Description = "Parses the provided text and returns parsed words and any gaps as separate items, preserving order.")]
    // [ProducesResponseType(typeof(List<DeckWordDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IResult> Parse(ReaderParseRequest request)
    {
        var totalLength = string.Join("", request.Text).Length;
        if (totalLength > 81000)
            return Results.BadRequest("Text is too long");

        var userId = currentUserService.UserId;
        if (userId != null && !parseThrottle.TryConsume(userId, totalLength))
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);

        List<List<ReaderToken>> allTokens = new();
        List<ReaderWord> allWords = new();
        List<List<DeckWord>> parsedParagraphs = new();

        const string stopToken = "\n|\n";
        var combinedText = string.Join(stopToken, request.Text);
        
        // The parsed text is different to add stop tokens to characters that break because sudachi would ignore them and parse as a whole word
        // But we need the original combined text for the paragraph logic position tracking
        var parsedText = combinedText.Replace(" ", stopToken);
        var allParsedWords = await Parser.Parser.ParseText(contextFactory, parsedText, preserveStopToken: true);

        var paragraphOffsets = new int[request.Text.Length];
        var currentOffset = 0;
        for (var i = 0; i < request.Text.Length; i++)
        {
            paragraphOffsets[i] = currentOffset;
            currentOffset += request.Text[i].Length + stopToken.Length;
        }

        var wordIndex = 0;
        var positionInCombined = 0;
        var positionCache = new Dictionary<int, int>();
        for (var i = 0; i < request.Text.Length; i++)
        {
            var paragraphWords = new List<DeckWord>();
            var paragraphEnd = paragraphOffsets[i] + request.Text[i].Length;

            while (wordIndex < allParsedWords.Count)
            {
                var word = allParsedWords[wordIndex];
                int wordPosition;
                if (positionCache.Remove(wordIndex, out var cached))
                {
                    wordPosition = cached;
                }
                else
                {
                    (wordPosition, _) = TokenPositionHelper.FindTokenInSource(combinedText, word.OriginalText, positionInCombined);
                }

                if (wordPosition < 0)
                {
                    wordIndex++;
                    continue;
                }

                var (_, wordSourceLength) = TokenPositionHelper.FindTokenInSource(combinedText, word.OriginalText, wordPosition);

                // Check if this match might be wrong (found too far ahead)
                // by looking for subsequent words between current position and found position
                if (wordPosition - positionInCombined > 10)
                {
                    var foundCloserWord = false;
                    for (var lookAhead = 1; lookAhead <= 5 && wordIndex + lookAhead < allParsedWords.Count; lookAhead++)
                    {
                        var futureIdx = wordIndex + lookAhead;
                        int futurePos;
                        if (positionCache.TryGetValue(futureIdx, out var cachedFuture))
                        {
                            futurePos = cachedFuture;
                        }
                        else
                        {
                            var futureWord = allParsedWords[futureIdx];
                            (futurePos, _) = TokenPositionHelper.FindTokenInSource(combinedText, futureWord.OriginalText, positionInCombined);
                            if (futurePos >= 0)
                                positionCache[futureIdx] = futurePos;
                        }

                        if (futurePos >= 0 && futurePos < wordPosition)
                        {
                            foundCloserWord = true;
                            break;
                        }
                    }

                    if (foundCloserWord)
                    {
                        wordIndex++;
                        continue;
                    }
                }

                if (wordPosition >= paragraphEnd)
                    break;

                if (wordPosition >= paragraphOffsets[i])
                {
                    if (wordSourceLength != word.OriginalText.Length)
                        word.OriginalText = combinedText.Substring(wordPosition, wordSourceLength);
                    paragraphWords.Add(word);
                    positionInCombined = wordPosition + wordSourceLength;
                }

                wordIndex++;
            }

            parsedParagraphs.Add(paragraphWords);
        }

        var wordIds = parsedParagraphs.SelectMany(p => p).Select(w => w.WordId).Distinct().ToList();
        var jmdictWords = await context.JMDictWords.Where(w => wordIds.Contains(w.WordId)).Include(w => w.Definitions.OrderBy(d => d.SenseIndex)).ToDictionaryAsync(w => w.WordId);
        var readerForms = await WordFormHelper.LoadWordForms(context, wordIds);
        var readerFormFreqs = await WordFormHelper.LoadWordFormFrequencies(context, wordIds);

        var distinctKeys = parsedParagraphs.SelectMany(p => p.Select(dw => (dw.WordId, dw.ReadingIndex))).Distinct().ToList();
        var knownStates = await currentUserService.GetKnownWordsState(distinctKeys);
        var deckMembershipMap = await deckMembership.GetDeckMembership(distinctKeys);

        for (var i = 0; i < parsedParagraphs.Count; i++)
        {
            List<DeckWord>? parsedWords = parsedParagraphs[i];
            List<ReaderToken> tokens = new();
            int currentPosition = 0;

            foreach (var word in parsedWords)
            {
                int position = request.Text[i].IndexOf(word.OriginalText, currentPosition, StringComparison.Ordinal);
                if (position >= 0)
                {
                    tokens.Add(new ReaderToken
                               {
                                   WordId = word.WordId, ReadingIndex = word.ReadingIndex, Start = position,
                                   End = position + word.OriginalText.Length, Length = word.OriginalText.Length,
                                   Conjugations = word.Conjugations
                               });
                    var jmdictWord = jmdictWords[word.WordId];
                    knownStates.TryGetValue((word.WordId, word.ReadingIndex), out var knownState);
                    deckMembershipMap.TryGetValue((word.WordId, word.ReadingIndex), out var studyDeckIds);
                    var rdrForm = readerForms.GetValueOrDefault((word.WordId, (short)word.ReadingIndex));
                    var rdrFormFreq = readerFormFreqs.GetValueOrDefault((word.WordId, (short)word.ReadingIndex));
                    var readerWord = new ReaderWord()
                                     {
                                         WordId = word.WordId, ReadingIndex = word.ReadingIndex,
                                         Spelling = rdrForm?.Text ?? "", Reading = rdrForm?.RubyText ?? "",
                                         PartsOfSpeech = jmdictWord.PartsOfSpeech.ToHumanReadablePartsOfSpeech(), MeaningsChunks =
                                             jmdictWord.Definitions.Where(d => d.EnglishMeanings.Count > 0)
                                                       .Select(d => d.EnglishMeanings).ToList(),
                                         MeaningsPartOfSpeech = jmdictWord.Definitions.SelectMany(d => d.PartsOfSpeech).ToList() ?? [""],
                                         FrequencyRank = rdrFormFreq?.FrequencyRank ?? 0,
                                         KnownState = knownState ?? [KnownState.New],
                                         PitchAccents = jmdictWord.PitchAccents ?? new(),
                                         StudyDeckIds = studyDeckIds ?? new(),
                                     };
                    allWords.Add(readerWord);

                    currentPosition = position + word.OriginalText.Length;
                }
            }

            allTokens.Add(tokens);
        }

        logger.LogInformation("Reader parsed text: ParagraphCount={ParagraphCount}, TotalWords={TotalWords}, TotalLength={TotalLength}",
                              request.Text.Length, parsedParagraphs.Sum(p => p.Count), string.Join("", request.Text).Length);
        return Results.Ok(new { tokens = allTokens, vocabulary = allWords });
    }

    [HttpPost("lookup-vocabulary")]
    [SwaggerOperation(Summary = "Lookup vocabulary known states",
                      Description = "Returns the known state for each word/reading combination for the authenticated user.")]
    public async Task<IResult> LookupVocabulary(LookupVocabularyRequest request)
    {
        var keys = request.Words.Select(w => (w[0], (byte)w[1])).ToList();
        var knownStates = await currentUserService.GetKnownWordsState(keys);
        var deckMembershipMap = await deckMembership.GetDeckMembership(keys);

        var result = request.Words.Select(w =>
                                              knownStates.TryGetValue((w[0], (byte)w[1]), out var state)
                                                  ? state.Select(s => (int)s)
                                                  : [0]
                                         ).ToList();

        var decks = request.Words.Select(w =>
                                             deckMembershipMap.TryGetValue((w[0], (byte)w[1]), out var deckIds)
                                                 ? deckIds
                                                 : new List<int>()
                                        ).ToList();

        return Results.Ok(new { result = result, decks = decks });
    }
}