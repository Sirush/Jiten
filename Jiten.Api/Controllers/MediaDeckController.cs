using System.Text;
using AnkiNet;
using Jiten.Api.Dtos;
using Jiten.Api.Enums;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WanaKanaShaapu;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/media-deck")]
[EnableRateLimiting("fixed")]
public class MediaDeckController(JitenDbContext context) : ControllerBase
{
    [HttpGet("get-media-decks-id")]
    [ResponseCache(Duration = 60 * 60 * 24)]
    public async Task<List<int>> GetMediaDecksId()
    {
        return await context.Decks.AsNoTracking().Where(d => d.ParentDeckId == null).Select(d => d.DeckId).ToListAsync();
    }

    [HttpGet("get-media-decks")]
    [ResponseCache(Duration = 300,
                   VaryByQueryKeys = ["offset", "mediaType", "wordId", "readingIndex", "titleFilter", "sortBy", "sortOrder"])]
    public async Task<PaginatedResponse<List<Deck>>> GetMediaDecks(int? offset = 0, MediaType? mediaType = null,
                                                                   int wordId = 0, int readingIndex = 0, string? titleFilter = "",
                                                                   string? sortBy = "",
                                                                   SortOrder sortOrder = SortOrder.Ascending)
    {
        int pageSize = 50;

        var query = context.Decks.AsNoTracking();

        query = query.Where(d => d.ParentDeckId == null);
        if (mediaType != null)
            query = query.Where(d => d.MediaType == mediaType);

        if (!string.IsNullOrEmpty(titleFilter))
        {
            query = query.Where(d =>
                                    // Fuzzy matching
                                    EF.Functions.FuzzyStringMatchLevenshtein(d.OriginalTitle, titleFilter) <= 3 ||
                                    EF.Functions.FuzzyStringMatchLevenshtein(d.RomajiTitle!, titleFilter) <= 3 ||
                                    EF.Functions.FuzzyStringMatchLevenshtein(d.EnglishTitle!, titleFilter) <= 3 ||

                                    // Substring matching
                                    EF.Functions.ILike(d.OriginalTitle, $"%{titleFilter}%") ||
                                    EF.Functions.ILike(d.RomajiTitle!, $"%{titleFilter}%") ||
                                    EF.Functions.ILike(d.EnglishTitle!, $"%{titleFilter}%")
                               );
        }

        if (wordId != 0)
        {
            // Execute this query first and materialize the results
            var deckIds = await context.DeckWords
                                       .Where(dw => dw.WordId == wordId && dw.ReadingIndex == readingIndex)
                                       .Select(dw => dw.DeckId)
                                       .Distinct()
                                       .ToListAsync(); // Get the IDs in memory first

            // Then use the in-memory collection for filtering
            query = query.Where(d => deckIds.Contains(d.DeckId));
        }

        var totalCount = query.Count();

        if (string.IsNullOrEmpty(sortBy))
            sortBy = "title";

        query = sortBy switch
        {
            "difficulty" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.Difficulty)
                : query.OrderByDescending(d => d.Difficulty),
            "charCount" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.CharacterCount)
                : query.OrderByDescending(d => d.CharacterCount),
            "sentenceLength" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.CharacterCount / (d.SentenceCount + 1))
                : query.OrderByDescending(d => d.CharacterCount / (d.SentenceCount + 1)),
            "wordCount" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.WordCount)
                : query.OrderByDescending(d => d.WordCount),
            "uKanji" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.UniqueKanjiCount)
                : query.OrderByDescending(d => d.UniqueKanjiCount),
            "uWordCount" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.UniqueWordCount)
                : query.OrderByDescending(d => d.UniqueWordCount),
            "uKanjiOnce" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.UniqueKanjiUsedOnceCount)
                : query.OrderByDescending(d => d.UniqueKanjiUsedOnceCount),
            _ => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.RomajiTitle)
                : query.OrderByDescending(d => d.RomajiTitle),
        };

        var decks = query
                    .Include(d => d.Links)
                    .Skip(offset ?? 0)
                    .Take(pageSize)
                    .ToList();

        return new PaginatedResponse<List<Deck>>(decks, totalCount, pageSize, offset ?? 0);
    }

    [HttpGet("{id}/vocabulary")]
    [ResponseCache(Duration = 600, VaryByQueryKeys = ["id", "sortBy", "sortOrder", "offset"])]
    public PaginatedResponse<DeckVocabularyListDto?> GetVocabulary(int id, string? sortBy = "", SortOrder sortOrder = SortOrder.Ascending,
                                                                   int? offset = 0)
    {
        int pageSize = 100;

        var deck = context.Decks.AsNoTracking().FirstOrDefault(d => d.DeckId == id);

        if (deck == null)
            return new PaginatedResponse<DeckVocabularyListDto?>(null, 0, pageSize, offset ?? 0);


        var query = context.DeckWords.AsNoTracking().Where(dw => dw.DeckId == id);

        query = sortBy switch
        {
            "globalFreq" => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => context.JmDictWordFrequencies
                                            .Where(f => f.WordId == d.WordId)
                                            .Select(f => f.ReadingsFrequencyRank[d.ReadingIndex])
                                            .FirstOrDefault())
                : query.OrderByDescending(d => context.JmDictWordFrequencies
                                                      .Where(f => f.WordId == d.WordId)
                                                      .Select(f => f.ReadingsFrequencyRank[d.ReadingIndex])
                                                      .FirstOrDefault()),
            "deckFreq" => sortOrder == SortOrder.Ascending
                ? query.OrderByDescending(d => d.Occurrences)
                : query.OrderBy(d => d.Occurrences),
            "chrono" or _ => sortOrder == SortOrder.Ascending
                ? query.OrderBy(d => d.DeckWordId)
                : query.OrderByDescending(d => d.DeckWordId),
        };

        int totalCount = query.Count(dw => dw.DeckId == id);

        var deckWordsList = query.Skip(offset ?? 0)
                                 .Take(pageSize)
                                 .ToList();

        var wordIds = deckWordsList.Select(dw => dw.WordId).ToList();

        var jmdictWords = context.JMDictWords.AsNoTracking()
                                 .Where(w => wordIds.Contains(w.WordId))
                                 .Include(w => w.Definitions)
                                 .ToList();

        var words = deckWordsList.Select(dw => new { dw, jmDictWord = jmdictWords.FirstOrDefault(w => w.WordId == dw.WordId) })
                                 .OrderBy(dw => wordIds.IndexOf(dw.dw.WordId))
                                 .ToList();

        var frequencies = context.JmDictWordFrequencies.AsNoTracking().Where(f => wordIds.Contains(f.WordId)).ToList();

        DeckVocabularyListDto dto = new() { Deck = deck, Words = new() };

        foreach (var word in words)
        {
            if (word.jmDictWord == null)
            {
                continue;
            }

            var reading = word.jmDictWord.ReadingsFurigana[word.dw.ReadingIndex];

            List<ReadingDto> alternativeReadings = word.jmDictWord.ReadingsFurigana
                                                       .Select((r, i) => new ReadingDto
                                                                         {
                                                                             Text = r, ReadingIndex = i,
                                                                             ReadingType = word.jmDictWord.ReadingTypes[i], FrequencyRank =
                                                                                 frequencies.First(f => f.WordId == word.dw.WordId)
                                                                                            .ReadingsFrequencyRank[i],
                                                                             FrequencyPercentage =
                                                                                 frequencies.First(f => f.WordId == word.dw.WordId)
                                                                                            .ReadingsFrequencyPercentage[i]
                                                                         })
                                                       .ToList();

            // Remove current
            alternativeReadings.RemoveAt(word.dw.ReadingIndex);

            var frequency = frequencies.First(f => f.WordId == word.dw.WordId);

            var mainReading = new ReadingDto()
                              {
                                  Text = reading, ReadingIndex = word.dw.ReadingIndex,
                                  ReadingType = word.jmDictWord.ReadingTypes[word.dw.ReadingIndex],
                                  FrequencyRank = frequency.ReadingsFrequencyRank[word.dw.ReadingIndex],
                                  FrequencyPercentage = frequency.ReadingsFrequencyPercentage[word.dw.ReadingIndex]
                              };

            var wordDto = new WordDto
                          {
                              WordId = word.jmDictWord.WordId, MainReading = mainReading, AlternativeReadings = alternativeReadings,
                              PartsOfSpeech = word.jmDictWord.PartsOfSpeech.ToHumanReadablePartsOfSpeech(),
                              Definitions = word.jmDictWord.Definitions.ToDefinitionDtos(), Occurences = word.dw.Occurrences
                          };

            dto.Words.Add(wordDto);
        }

        return new PaginatedResponse<DeckVocabularyListDto?>(dto, totalCount, pageSize, offset ?? 0);
    }

    [HttpGet("{id}/detail")]
    [ResponseCache(Duration = 600, VaryByQueryKeys = ["id", "offset"])]
    public PaginatedResponse<DeckDetailDto?> GetMediaDeckDetail(int id, int? offset = 0)
    {
        int pageSize = 25;

        var deck = context.Decks.AsNoTracking().FirstOrDefault(d => d.DeckId == id);

        if (deck == null)
            return new PaginatedResponse<DeckDetailDto?>(null, 0, pageSize, offset ?? 0);

        var subDecks = context.Decks.AsNoTracking()
                              .Where(d => d.ParentDeckId == id);
        int totalCount = subDecks.Count();

        subDecks = subDecks
                   .OrderBy(dw => dw.DeckOrder)
                   .Skip(offset ?? 0)
                   .Take(pageSize);

        var dto = new DeckDetailDto { MainDeck = deck, SubDecks = subDecks.ToList() };

        return new PaginatedResponse<DeckDetailDto?>(dto, totalCount, pageSize, offset ?? 0);
    }

    [HttpGet("{id}/download")]
    [EnableRateLimiting("download")]
    public async Task<IResult> DownloadDeck(int id, DeckFormat format, DeckDownloadType downloadType, DeckOrder order,
                                            int minFrequency = 0, int maxFrequency = 0)
    {
        var deck = context.Decks.AsNoTracking().FirstOrDefault(d => d.DeckId == id);

        if (deck == null)
        {
            return Results.NotFound();
        }

        IQueryable<DeckWord> deckWordsQuery = context.DeckWords.AsNoTracking().Where(dw => dw.DeckId == id);

        switch (downloadType)
        {
            case DeckDownloadType.Full:
                break;

            case DeckDownloadType.TopGlobalFrequency:
                // Get the words between frequency rank minFrequency and maxFrequency
                deckWordsQuery = deckWordsQuery.Where(dw => context.JmDictWordFrequencies
                                                                   .Any(f => f.WordId == dw.WordId &&
                                                                             f.ReadingsFrequencyRank[dw.ReadingIndex] >= minFrequency &&
                                                                             f.ReadingsFrequencyRank[dw.ReadingIndex] <= maxFrequency));
                break;

            case DeckDownloadType.TopDeckFrequency:
                deckWordsQuery = deckWordsQuery
                                 .OrderBy(dw => dw.Occurrences)
                                 .Skip(minFrequency)
                                 .Take(maxFrequency - minFrequency);
                break;

            case DeckDownloadType.TopChronological:
                deckWordsQuery = deckWordsQuery
                                 .OrderBy(dw => dw.DeckWordId)
                                 .Skip(minFrequency)
                                 .Take(maxFrequency - minFrequency);
                break;
            default:
                return Results.BadRequest();
        }

        switch (order)
        {
            case DeckOrder.Chronological:
                deckWordsQuery = deckWordsQuery.OrderBy(dw => dw.DeckWordId);
                break;

            case DeckOrder.GlobalFrequency:
                deckWordsQuery = deckWordsQuery.OrderBy(dw => context.JmDictWordFrequencies
                                                                     .Where(f => f.WordId == dw.WordId)
                                                                     .Select(f => f.ReadingsFrequencyRank[dw.ReadingIndex])
                                                                     .FirstOrDefault()
                                                       );
                break;

            case DeckOrder.DeckFrequency:
                deckWordsQuery = deckWordsQuery.OrderBy(dw => dw.Occurrences);
                break;
            default:
                return Results.BadRequest();
        }

        var wordIds = await deckWordsQuery.Select(dw => dw.WordId).ToListAsync();
        var deckWords = await deckWordsQuery.Select(dw => new { dw.WordId, dw.ReadingIndex }).ToListAsync();

        var jmdictWords = await context.JMDictWords.AsNoTracking()
                                       .Include(w => w.Definitions)
                                       .Where(w => wordIds.Contains(w.WordId))
                                       .ToDictionaryAsync(w => w.WordId);

        switch (format)
        {
            case DeckFormat.Anki:
                // Lapis template from https://github.com/donkuri/lapis/tree/main
                var templatePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "lapis.apkg");
                var template = await AnkiFileReader.ReadFromFileAsync(templatePath);
                var noteTypeTemplate = template.NoteTypes.First();

                var collection = new AnkiCollection();
                var noteTypeId = collection.CreateNoteType(noteTypeTemplate);
                var deckId = collection.CreateDeck(deck.OriginalTitle);

                foreach (var word in deckWords)
                {
                    string expression = jmdictWords[word.WordId].Readings[word.ReadingIndex];
                    string expressionFurigana = jmdictWords[word.WordId].ReadingsFurigana[word.ReadingIndex];
                    // Very unoptimized, might have to rework
                    string expressionReading = string.Join("", jmdictWords[word.WordId].ReadingsFurigana[word.ReadingIndex]
                                                                                       .Where(c => WanaKana.IsKana(c.ToString()))
                                                                                       .Select(c => c.ToString()));
                    string expressionAudio = "";
                    string selectionText = "";
                    string mainDefinition = "<ul>" +
                                            string.Join("", jmdictWords[word.WordId].Definitions
                                                                                    .SelectMany(d => d.EnglishMeanings)
                                                                                    .Select(meaning => $"<li>{meaning}</li>")) +
                                            "</ul>";
                    string definitionPicture = "";
                    string sentence = "";
                    string sentenceFurigana = "";
                    string sentenceAudio = "";
                    string picture = "";
                    // This is where to add extra definitions, such as J-J
                    string glossary = "";
                    string hint = "";
                    string isWordAndSentenceCard = "";
                    string isClickCard = "";
                    string isSentenceCard = "";
                    string pitchPosition = "";
                    string pitchCategories = "";
                    string frequency = "";
                    string freqSort = "";
                    string miscInfo = $"From {deck.OriginalTitle} - generated by Jiten.moe";


                    collection.CreateNote(deckId, noteTypeId,
                                          expression, expressionFurigana,
                                          expressionReading, expressionAudio, selectionText, mainDefinition, definitionPicture,
                                          sentence, sentenceFurigana,
                                          sentenceAudio, picture, glossary, hint,
                                          isWordAndSentenceCard, isClickCard, isSentenceCard,
                                          pitchPosition, pitchCategories,
                                          frequency, freqSort,
                                          miscInfo
                                         );
                }


                var stream = new MemoryStream();

                await AnkiFileWriter.WriteToStreamAsync(stream, collection);
                var bytes = stream.ToArray();

                return Results.File(bytes, "application/x-binary", $"{deck.OriginalTitle}.apkg");

            case DeckFormat.Csv:
                StringBuilder sb = new StringBuilder();
                foreach (var word in deckWords)
                {
                    string reading = jmdictWords[word.WordId].Readings[word.ReadingIndex];
                    string readingFurigana = jmdictWords[word.WordId].ReadingsFurigana[word.ReadingIndex];
                    // Very unoptimized, might have to rework
                    string readingKana = string.Join("", jmdictWords[word.WordId].ReadingsFurigana[word.ReadingIndex]
                                                                                 .Where(c => WanaKana.IsKana(c.ToString()))
                                                                                 .Select(c => c.ToString()));
                    string definitions = string.Join(",", jmdictWords[word.WordId].Definitions
                                                                                  .SelectMany(d => d.EnglishMeanings)
                                                                                  .Select(m => m.Replace("\"", "\"\"")));

                    sb.AppendLine($"\"{reading}\",\"{readingFurigana}\",\"{readingKana}\",\"{definitions}\"");
                }

                return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"{deck.OriginalTitle}.csv");
            default:
                return Results.BadRequest();
        }

        return Results.BadRequest();
    }

    [HttpGet("decks-count")]
    [ResponseCache(Duration = 600)]
    public IResult GetDecksCountByMediaType()
    {
        Dictionary<int, int> decksCount = context.Decks.AsNoTracking()
                                                 .Where(d => d.ParentDeckId == null)
                                                 .GroupBy(d => d.MediaType)
                                                 .ToDictionary(g => (int)g.Key, g => g.Count());

        return Results.Ok(decksCount);
    }
}