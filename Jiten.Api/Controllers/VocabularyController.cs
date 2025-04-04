using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/vocabulary")]
[EnableRateLimiting("fixed")]
public class VocabularyController(JitenDbContext context) : ControllerBase
{
    [HttpGet("{wordId}/{readingIndex}")]
    [ResponseCache(Duration = 3600)]
    public async Task<IResult> GetWord(int wordId, int readingIndex)
    {
        var word = await context.JMDictWords.AsNoTracking()
                                .Include(w => w.Definitions)
                                .FirstOrDefaultAsync(w => w.WordId == wordId);

        if (word == null)
            return Results.NotFound();

        var frequency = context.JmDictWordFrequencies.AsNoTracking().First(f => f.WordId == word.WordId);

        var usedInMediaByType = await context.DeckWords.AsNoTracking()
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

        var mainReading = new ReadingDto()
                          {
                              Text = word.ReadingsFurigana[readingIndex], ReadingIndex = readingIndex,
                              ReadingType = word.ReadingTypes[readingIndex], FrequencyRank = frequency.ReadingsFrequencyRank[readingIndex],
                              FrequencyPercentage = frequency.ReadingsFrequencyPercentage[readingIndex].ZeroIfNaN(),
                              UsedInMediaAmount = frequency.ReadingsUsedInMediaAmount[readingIndex],
                              UsedInMediaAmountByType = usedInMediaByType
                          };

        List<ReadingDto> alternativeReadings = word.Readings
                                                   .Select((r, i) => new ReadingDto
                                                                     {
                                                                         Text = r, ReadingIndex = i, ReadingType = word.ReadingTypes[i],
                                                                         FrequencyRank =
                                                                             frequency.ReadingsFrequencyRank[i],
                                                                         FrequencyPercentage =
                                                                             frequency.ReadingsFrequencyPercentage[i].ZeroIfNaN(),
                                                                         UsedInMediaAmount = frequency.ReadingsUsedInMediaAmount[i]
                                                                     })
                                                   .ToList();

        return Results.Ok(new WordDto
                          {
                              WordId = word.WordId, MainReading = mainReading, AlternativeReadings = alternativeReadings,
                              Definitions = word.Definitions.ToDefinitionDtos(), PartsOfSpeech = word.PartsOfSpeech,
                              PitchAccents = word.PitchAccents
                          });
    }

    [HttpGet("parse")]
    public async Task<IResult> Parse(string text)
    {
        if (text.Length > 200)
            return Results.BadRequest("Text is too long");

        var parsedWords = await Parser.Program.ParseText(context, text);

        // We want both parsed words and unparsed ones
        var allWords = new List<DeckWordDto>();

        var wordsWithPositions = new List<(DeckWordDto Word, int Position)>();
        int currentPosition = 0;

        foreach (var word in parsedWords)
        {
            int position = text.IndexOf(word.OriginalText, currentPosition, StringComparison.Ordinal);
            if (position >= 0)
            {
                wordsWithPositions.Add((new DeckWordDto(word), position));
                currentPosition = position + word.OriginalText.Length;
            }
        }

        currentPosition = 0;
        foreach (var (word, position) in wordsWithPositions)
        {
            // If there's a gap before this word, add it as an unparsed word
            if (position > currentPosition)
            {
                string gap = text.Substring(currentPosition, position - currentPosition);
                allWords.Add(new DeckWordDto(gap));
            }

            allWords.Add(word);

            currentPosition = position + word.OriginalText.Length;
        }

        if (currentPosition < text.Length)
        {
            string gap = text.Substring(currentPosition);
            allWords.Add(new DeckWordDto(gap));
        }

        return Results.Ok(allWords);
    }
}