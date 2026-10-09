using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

public readonly record struct VocabularyRow(int WordId, byte ReadingIndex, int Occurrences);

public static class VocabularyWordListBuilder
{
    /// <summary>Word DTOs for one page of vocabulary rows, with the viewer's known states; rows without a word or form are dropped.</summary>
    public static async Task<List<WordDto>> BuildAsync(JitenDbContext context, IFrequencySourceResolver frequencySourceResolver,
                                                       ICurrentUserService currentUserService, IReadOnlyList<VocabularyRow> rows,
                                                       MediaType? frequencySource)
    {
        var wordIds = rows.Select(r => r.WordId).ToList();
        var uniqueWordIds = wordIds.Distinct().ToList();

        var jmdictWordsDict = await context.JMDictWords.AsNoTracking()
                                           .Where(w => uniqueWordIds.Contains(w.WordId))
                                           .Include(w => w.Definitions.OrderBy(d => d.SenseIndex))
                                           .ToDictionaryAsync(w => w.WordId);

        var wordIdOrder = new Dictionary<int, int>(capacity: wordIds.Count);
        for (int i = 0; i < wordIds.Count; i++)
        {
            wordIdOrder.TryAdd(wordIds[i], i);
        }

        var words = rows.Select(row => new { row, jmDictWord = jmdictWordsDict.GetValueOrDefault(row.WordId) })
                        .OrderBy(w => wordIdOrder.GetValueOrDefault(w.row.WordId, int.MaxValue))
                        .ToList();

        var forms = await WordFormHelper.LoadWordForms(context, uniqueWordIds);
        var scopedFreqs = await frequencySourceResolver.LoadFrequencies(context, uniqueWordIds, new FrequencyScope(frequencySource, null));

        var knownWords = await currentUserService.GetKnownWordsState(words.Select(w => (w.row.WordId, w.row.ReadingIndex)).ToList());

        var result = new List<WordDto>(words.Count);
        foreach (var word in words)
        {
            if (word.jmDictWord == null)
            {
                continue;
            }

            var key = (word.row.WordId, (short)word.row.ReadingIndex);
            var mainForm = forms.GetValueOrDefault(key);
            if (mainForm == null) continue;

            var allFormsForWord = forms.Where(f => f.Key.Item1 == word.row.WordId)
                                       .OrderBy(f => f.Key.Item2)
                                       .Select(f => f.Value)
                                       .ToList();

            List<WordFormDto> alternativeReadings = allFormsForWord
                                                    .Where(f => f.ReadingIndex != word.row.ReadingIndex)
                                                    .Select(f =>
                                                                WordFormHelper.ToPlainFormDto(f, scopedFreqs.Resolve(f.WordId, f.ReadingIndex)))
                                                    .ToList();

            var mainReading = WordFormHelper.ToFormDto(mainForm, scopedFreqs.Resolve(key.Item1, key.Item2));

            result.Add(new WordDto
            {
                WordId = word.jmDictWord.WordId, MainReading = mainReading, AlternativeReadings = alternativeReadings,
                PartsOfSpeech = word.jmDictWord.PartsOfSpeech.ToHumanReadablePartsOfSpeech(),
                Definitions = word.jmDictWord.Definitions.ToDefinitionDtos(), Occurrences = word.row.Occurrences,
                PitchAccents = word.jmDictWord.PitchAccents
            });
        }

        result.ApplyKnownWordsState(knownWords);
        return result;
    }
}
