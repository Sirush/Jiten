using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

/// <summary>
/// Endpoints for working with kanji characters.
/// </summary>
[ApiController]
[Route("api/kanji")]
[EnableRateLimiting("fixed")]
[Produces("application/json")]
public class KanjiController(JitenDbContext context) : ControllerBase
{
    /// <summary>
    /// Gets a kanji by its character, including readings, meanings, metadata and top words.
    /// </summary>
    /// <param name="character">The kanji character.</param>
    /// <returns>The full kanji data with top words.</returns>
    [HttpGet("{character}")]
    [SwaggerOperation(Summary = "Get kanji by character",
                      Description =
                          "Returns a kanji with readings, meanings, stroke count, JLPT level, grade, frequency rank, top 20 words containing it, its components, the kanji it appears in, and its stroke order. Component and stroke data come from KanjiVG (CC BY-SA 3.0).")]
    [ProducesResponseType(typeof(KanjiDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ResponseCache(Duration = 3600)]
    public async Task<IResult> GetKanji([FromRoute] string character)
    {
        var kanji = await context.Kanjis
                                 .AsNoTracking()
                                 .FirstOrDefaultAsync(k => k.Character == character);

        if (kanji == null)
            return Results.NotFound();

        // Top 20 words selected in SQL via join + PostgreSQL array indexing
        var topWordData = await context.WordKanjis
                                        .AsNoTracking()
                                        .Where(wk => wk.KanjiCharacter == character)
                                        .Select(wk => new { wk.WordId, wk.ReadingIndex })
                                        .Distinct()
                                        .Join(context.WordFormFrequencies.AsNoTracking(),
                                              wk => new { wk.WordId, ReadingIndex = (short)wk.ReadingIndex },
                                              wff => new { wff.WordId, wff.ReadingIndex },
                                              (wk, wff) => new { wk.WordId, wk.ReadingIndex, Rank = (int?)wff.FrequencyRank })
                                        .Where(x => x.Rank > 0)
                                        .OrderBy(x => x.Rank)
                                        .Take(20)
                                        .ToListAsync();

        var topWordIds = topWordData.Select(x => x.WordId).Distinct().ToList();
        var words = await context.JMDictWords
                                 .AsNoTracking()
                                 .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
                                 .Where(w => topWordIds.Contains(w.WordId))
                                 .ToDictionaryAsync(w => w.WordId);

        var forms = await WordFormHelper.LoadWordForms(context, topWordIds);

        var topWords = topWordData
                       .Where(x => words.ContainsKey(x.WordId))
                       .Select(x =>
                       {
                           var word = words[x.WordId];
                           var form = forms.GetValueOrDefault((x.WordId, (short)x.ReadingIndex));
                           var mainDefinition = word.Definitions.FirstOrDefault()?.EnglishMeanings.FirstOrDefault();
                           return new WordSummaryDto
                                  {
                                      WordId = x.WordId, ReadingIndex = (byte)x.ReadingIndex, Reading = form?.Text ?? "",
                                      ReadingFurigana = form?.RubyText ?? "", MainDefinition = mainDefinition,
                                      FrequencyRank = x.Rank!.Value
                                  };
                       })
                       .ToList();

        // Words grouped by reading (top 5 per reading, ordered by frequency-weighted reading importance)
        var readingWordData = await context.KanjiReadingWords
            .AsNoTracking()
            .Where(krw => krw.KanjiCharacter == character)
            .Join(context.WordFormFrequencies.AsNoTracking(),
                  krw => new { krw.WordId, ReadingIndex = (short)krw.ReadingIndex },
                  wff => new { wff.WordId, wff.ReadingIndex },
                  (krw, wff) => new { krw.Reading, krw.WordId, krw.ReadingIndex, Rank = (int?)wff.FrequencyRank })
            .Where(x => x.Rank > 0)
            .ToListAsync();

        var readingGroups = readingWordData
            .GroupBy(x => x.Reading)
            .Select(g => new
            {
                Reading = g.Key,
                TotalWords = g.Count(),
                TopEntries = g.OrderBy(x => x.Rank).Take(10).ToList(),
                BestRank = g.Min(x => x.Rank ?? int.MaxValue)
            })
            .OrderByDescending(g => g.TotalWords)
            .ToList();

        var readingWordIds = readingGroups.SelectMany(g => g.TopEntries.Select(e => e.WordId)).Distinct().ToList();
        var readingWords = readingWordIds.Count > 0
            ? await context.JMDictWords.AsNoTracking()
                .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
                .Where(w => readingWordIds.Contains(w.WordId))
                .ToDictionaryAsync(w => w.WordId)
            : new Dictionary<int, Core.Data.JMDict.JmDictWord>();

        var readingForms = readingWordIds.Count > 0
            ? await WordFormHelper.LoadWordForms(context, readingWordIds)
            : new Dictionary<(int, short), Core.Data.JMDict.JmDictWordForm>();

        var wordsByReading = readingGroups.Select(g => new KanjiReadingWordsDto
        {
            Reading = g.Reading,
            TotalWords = g.TotalWords,
            Words = g.TopEntries
                .Where(e => readingWords.ContainsKey(e.WordId))
                .Select(e =>
                {
                    var word = readingWords[e.WordId];
                    var form = readingForms.GetValueOrDefault((e.WordId, (short)e.ReadingIndex));
                    return new WordSummaryDto
                    {
                        WordId = e.WordId, ReadingIndex = (byte)e.ReadingIndex,
                        Reading = form?.Text ?? "", ReadingFurigana = form?.RubyText ?? "",
                        MainDefinition = word.Definitions.FirstOrDefault()?.EnglishMeanings.FirstOrDefault(),
                        FrequencyRank = e.Rank
                    };
                })
                .ToList()
        }).ToList();

        var (components, nestedRadical) = await LoadComponents(character);

        var usedInQuery = UsedInQuery(character);
        var usedInTotal = await usedInQuery.CountAsync();
        var usedIn = usedInTotal > 0 ? await ToUsedInDtos(usedInQuery.Take(UsedInPreviewCount)) : [];

        var strokes = await context.KanjiStrokes
                                   .AsNoTracking()
                                   .Where(s => s.Character == character)
                                   .Select(s => new KanjiStrokesDto { Paths = s.Paths, NumberPositions = s.NumberPositions })
                                   .FirstOrDefaultAsync();

        return Results.Ok(new KanjiDto
                          {
                              Character = kanji.Character, OnReadings = kanji.OnReadings, KunReadings = kanji.KunReadings,
                              Meanings = kanji.Meanings, StrokeCount = kanji.StrokeCount, JlptLevel = kanji.JlptLevel, Grade = kanji.Grade,
                              FrequencyRank = kanji.FrequencyRank, TopWords = topWords, WordsByReading = wordsByReading,
                              Components = components, NestedRadical = nestedRadical, UsedIn = usedIn, UsedInTotal = usedInTotal, Strokes = strokes
                          });
    }

    /// <summary>
    /// Gets every kanji that contains a given kanji as a component, at any depth.
    /// </summary>
    /// <param name="character">The kanji character.</param>
    /// <returns>Kanji ordered by frequency, then stroke count.</returns>
    [HttpGet("{character}/used-in")]
    [SwaggerOperation(Summary = "Get kanji containing a component",
                      Description = "Returns every kanji that contains the specified kanji as a component, most frequent first. Component data comes from KanjiVG (CC BY-SA 3.0).")]
    [ProducesResponseType(typeof(List<KanjiUsedInDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ResponseCache(Duration = 3600)]
    public async Task<IResult> GetKanjiUsedIn([FromRoute] string character)
    {
        var kanjiExists = await context.Kanjis.AnyAsync(k => k.Character == character);
        if (!kanjiExists)
            return Results.NotFound();

        return Results.Ok(await ToUsedInDtos(UsedInQuery(character)));
    }

    /// <summary>
    /// Gets a paginated list of words containing a specific kanji.
    /// </summary>
    /// <param name="character">The kanji character.</param>
    /// <param name="page">Page number (1-based).</param>
    /// <returns>Paginated list of words.</returns>
    [HttpGet("{character}/words")]
    [SwaggerOperation(Summary = "Get words containing kanji",
                      Description =
                          "Returns a paginated list of words containing the specified kanji, ordered by frequency. Optionally filter by kanji reading.")]
    [ProducesResponseType(typeof(PaginatedResponse<List<WordSummaryDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ResponseCache(Duration = 3600, VaryByQueryKeys = ["page", "pageSize", "reading"])]
    public async Task<IResult> GetKanjiWords(
        [FromRoute] string character,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        [FromQuery] string? reading = null)
    {
        var kanjiExists = await context.Kanjis.AnyAsync(k => k.Character == character);
        if (!kanjiExists)
            return Results.NotFound();

        pageSize = Math.Clamp(pageSize, 1, 5000);
        page = Math.Max(page, 1);

        IQueryable<WordRankResult> rankedQuery;

        if (!string.IsNullOrEmpty(reading))
        {
            rankedQuery = context.KanjiReadingWords
                .AsNoTracking()
                .Where(krw => krw.KanjiCharacter == character && krw.Reading == reading)
                .Select(krw => new { krw.WordId, krw.ReadingIndex })
                .Distinct()
                .Join(context.WordFormFrequencies.AsNoTracking(),
                      krw => new { krw.WordId, ReadingIndex = (short)krw.ReadingIndex },
                      wff => new { wff.WordId, wff.ReadingIndex },
                      (krw, wff) => new WordRankResult { WordId = krw.WordId, ReadingIndex = krw.ReadingIndex, Rank = wff.FrequencyRank })
                .Where(x => x.Rank > 0);
        }
        else
        {
            rankedQuery = context.WordKanjis
                .AsNoTracking()
                .Where(wk => wk.KanjiCharacter == character)
                .Select(wk => new { wk.WordId, wk.ReadingIndex })
                .Distinct()
                .Join(context.WordFormFrequencies.AsNoTracking(),
                      wk => new { wk.WordId, ReadingIndex = (short)wk.ReadingIndex },
                      wff => new { wff.WordId, wff.ReadingIndex },
                      (wk, wff) => new WordRankResult { WordId = wk.WordId, ReadingIndex = wk.ReadingIndex, Rank = wff.FrequencyRank })
                .Where(x => x.Rank > 0);
        }

        var totalCount = await rankedQuery.CountAsync();

        var pageData = await rankedQuery
                              .OrderBy(x => x.Rank)
                              .Skip((page - 1) * pageSize)
                              .Take(pageSize)
                              .ToListAsync();

        var pageWordIds = pageData.Select(x => x.WordId).Distinct().ToList();
        var words = await context.JMDictWords
                                 .AsNoTracking()
                                 .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
                                 .Where(w => pageWordIds.Contains(w.WordId))
                                 .ToDictionaryAsync(w => w.WordId);

        var forms = await WordFormHelper.LoadWordForms(context, pageWordIds);

        var items = pageData
                    .Where(x => words.ContainsKey(x.WordId))
                    .Select(x =>
                    {
                        var word = words[x.WordId];
                        var form = forms.GetValueOrDefault((x.WordId, (short)x.ReadingIndex));
                        var mainDefinition = word.Definitions.FirstOrDefault()?.EnglishMeanings.FirstOrDefault();
                        return new WordSummaryDto
                               {
                                   WordId = x.WordId, ReadingIndex = (byte)x.ReadingIndex, Reading = form?.Text ?? "",
                                   ReadingFurigana = form?.RubyText ?? "", MainDefinition = mainDefinition,
                                   FrequencyRank = x.Rank
                               };
                    })
                    .ToList();

        return Results.Ok(new PaginatedResponse<List<WordSummaryDto>>(
                                                                      items,
                                                                      totalCount,
                                                                      pageSize,
                                                                      (page - 1) * pageSize
                                                                     ));
    }

    /// <summary>
    /// Returns kanji characters that appear in at least <see cref="MinWordsForSitemap"/> distinct words,
    /// for sitemap generation. Filtering out rarely-used kanji avoids indexing thin pages.
    /// </summary>
    [HttpGet("sitemap-characters")]
    [SwaggerOperation(Summary = "Get kanji characters for sitemap generation")]
    [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
    [ResponseCache(Duration = 60 * 60 * 24)]
    public async Task<List<string>> GetSitemapCharacters()
    {
        const int MinWordsForSitemap = 10;
        return await context.WordKanjis.AsNoTracking()
                            .GroupBy(wk => wk.KanjiCharacter)
                            .Where(g => g.Select(wk => wk.WordId).Distinct().Count() >= MinWordsForSitemap)
                            .Select(g => g.Key)
                            .ToListAsync();
    }

    private const int UsedInPreviewCount = 30;

    private async Task<(List<KanjiComponentDto> Components, KanjiNestedRadicalDto? NestedRadical)> LoadComponents(string character)
    {
        var tree = await context.KanjiComponents
                                .AsNoTracking()
                                .Where(c => c.KanjiCharacter == character)
                                .OrderBy(c => c.NodeIndex)
                                .ToListAsync();
        if (tree.Count == 0)
            return ([], null);

        var nodes = tree.Where(n => n.ParentIndex == null).DistinctBy(n => n.Component).ToList();
        var nestedRadical = nodes.Any(n => n.IsRadical) ? null : tree.FirstOrDefault(n => n.IsRadical && n.ParentIndex != null);

        var shown = nestedRadical == null ? nodes : nodes.Append(nestedRadical).ToList();
        var candidates = shown.Select(n => n.Component)
                              .Concat(shown.Where(n => n.Original != null).Select(n => n.Original!))
                              .Distinct()
                              .ToList();
        var meanings = await context.Kanjis
                                    .AsNoTracking()
                                    .Where(k => candidates.Contains(k.Character))
                                    .Select(k => new { k.Character, k.Meanings })
                                    .ToDictionaryAsync(k => k.Character, k => k.Meanings.FirstOrDefault());

        string? LinkFor(Core.Data.JMDict.KanjiComponent n) =>
            meanings.ContainsKey(n.Component) ? n.Component
            : n.Original != null && meanings.ContainsKey(n.Original) ? n.Original
            : null;

        var components = nodes.Select(n =>
                              {
                                  var link = LinkFor(n);
                                  return new KanjiComponentDto
                                         {
                                             Character = n.Component, Original = n.Original, LinkCharacter = link,
                                             Meaning = link != null ? meanings[link] : null, IsRadical = n.IsRadical, IsPhonetic = n.IsPhonetic
                                         };
                              })
                              .ToList();

        if (nestedRadical == null)
            return (components, null);

        var top = nestedRadical;
        while (top.ParentIndex != null)
            top = tree[top.ParentIndex.Value];

        var radicalLink = LinkFor(nestedRadical);
        return (components, new KanjiNestedRadicalDto
                            {
                                Character = nestedRadical.Component, Original = nestedRadical.Original, LinkCharacter = radicalLink,
                                Meaning = radicalLink != null ? meanings[radicalLink] : null, Inside = top.Component
                            });
    }

    // Matching Original too lists 休 (亻, a variant of 人) under 人.
    private IQueryable<Core.Data.JMDict.Kanji> UsedInQuery(string character) =>
        context.Kanjis
               .AsNoTracking()
               .Where(k => k.Character != character &&
                           context.KanjiComponents.Any(c => c.KanjiCharacter == k.Character &&
                                                            (c.Component == character || c.Original == character)))
               .OrderBy(k => k.FrequencyRank == null)
               .ThenBy(k => k.FrequencyRank)
               .ThenBy(k => k.StrokeCount)
               .ThenBy(k => k.Character);

    private static async Task<List<KanjiUsedInDto>> ToUsedInDtos(IQueryable<Core.Data.JMDict.Kanji> query)
    {
        var rows = await query.Select(k => new { k.Character, k.Meanings }).ToListAsync();
        return rows.Select(r => new KanjiUsedInDto { Character = r.Character, Meaning = r.Meanings.FirstOrDefault() }).ToList();
    }

    private class WordRankResult
    {
        public int WordId { get; set; }
        public short ReadingIndex { get; set; }
        public int Rank { get; set; }
    }
}