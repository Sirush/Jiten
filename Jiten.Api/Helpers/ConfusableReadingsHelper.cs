using Jiten.Core;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Helpers;

public static class ConfusableReadingsHelper
{
    public static async Task<Dictionary<(int, byte), List<string>>> LoadBatchConfusableReadings(
        IDbContextFactory<JitenDbContext> factory, List<(int WordId, byte ReadingIndex)> wordPairs)
    {
        if (wordPairs.Count == 0) return new();

        await using var ctx = await factory.CreateDbContextAsync();

        var pairWordIds = wordPairs.Select(p => p.WordId).Distinct().ToList();
        var pairReadingIndexes = wordPairs.ToHashSet();

        var kanjiTexts = await ctx.WordForms.AsNoTracking()
            .Where(wf => pairWordIds.Contains(wf.WordId)
                      && wf.FormType == JmDictFormType.KanjiForm
                      && !wf.IsSearchOnly)
            .Select(wf => new { wf.WordId, wf.ReadingIndex, wf.Text })
            .ToListAsync();

        kanjiTexts = kanjiTexts
            .Where(kt => pairReadingIndexes.Contains((kt.WordId, (byte)kt.ReadingIndex)))
            .ToList();

        if (kanjiTexts.Count == 0) return new();

        var sourceKanaForms = await ctx.WordForms.AsNoTracking()
            .Where(wf => pairWordIds.Contains(wf.WordId)
                      && wf.FormType == JmDictFormType.KanaForm
                      && !wf.IsSearchOnly)
            .Select(wf => new { wf.WordId, wf.ReadingIndex, wf.Text })
            .ToListAsync();

        var sourceReadings = sourceKanaForms
            .Where(kf => pairReadingIndexes.Contains((kf.WordId, (byte)kf.ReadingIndex)))
            .GroupBy(kf => (kf.WordId, (byte)kf.ReadingIndex))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Text).ToHashSet());

        var distinctTexts = kanjiTexts.Select(kt => kt.Text).Distinct().ToList();
        var textToSourcePairs = kanjiTexts
            .GroupBy(kt => kt.Text)
            .ToDictionary(g => g.Key, g => g.Select(x => (x.WordId, (byte)x.ReadingIndex)).Distinct().ToList());

        var allLookups = await ctx.Lookups.AsNoTracking()
            .Where(l => distinctTexts.Contains(l.LookupKey))
            .Select(l => new { l.LookupKey, l.WordId })
            .ToListAsync();

        var confusableWordIds = new HashSet<int>();
        var sourceToConfusable = new Dictionary<(int, byte), HashSet<int>>();

        foreach (var lookup in allLookups)
        {
            if (!textToSourcePairs.TryGetValue(lookup.LookupKey, out var sourcePairs)) continue;
            foreach (var sourcePair in sourcePairs)
            {
                if (lookup.WordId == sourcePair.WordId) continue;
                confusableWordIds.Add(lookup.WordId);
                if (!sourceToConfusable.TryGetValue(sourcePair, out var set))
                {
                    set = new HashSet<int>();
                    sourceToConfusable[sourcePair] = set;
                }
                set.Add(lookup.WordId);
            }
        }

        if (confusableWordIds.Count == 0) return new();

        var confusableIds = confusableWordIds.ToList();

        await using var ctx3 = await factory.CreateDbContextAsync();
        await using var ctx4 = await factory.CreateDbContextAsync();
        await using var ctx5 = await factory.CreateDbContextAsync();

        var wordsTask = ctx3.JMDictWords.AsNoTracking()
            .Where(w => confusableIds.Contains(w.WordId) && w.Priorities.Contains("name"))
            .Select(w => w.WordId)
            .ToListAsync();
        var formsTask = ctx4.WordForms.AsNoTracking()
            .Where(wf => confusableIds.Contains(wf.WordId)
                      && wf.FormType == JmDictFormType.KanaForm
                      && !wf.IsSearchOnly)
            .Select(wf => new { wf.WordId, wf.ReadingIndex, wf.Text })
            .ToListAsync();
        var freqsTask = ctx5.WordFormFrequencies.AsNoTracking()
            .Where(wff => confusableIds.Contains(wff.WordId) && wff.UsedInMediaAmount > 0)
            .Select(wff => new { wff.WordId, wff.ReadingIndex, wff.FrequencyRank })
            .ToListAsync();

        await Task.WhenAll(wordsTask, formsTask, freqsTask);

        var nameWordIds = (await wordsTask).ToHashSet();
        var forms = await formsTask;
        var freqs = await freqsTask;

        var freqPairs = freqs.Select(f => (f.WordId, f.ReadingIndex)).ToHashSet();
        var freqByWord = new Dictionary<int, int>();
        foreach (var f in freqs)
            freqByWord.TryAdd(f.WordId, f.FrequencyRank);

        var validReadings = new Dictionary<int, string>();
        foreach (var form in forms)
        {
            if (nameWordIds.Contains(form.WordId)) continue;
            if (!freqPairs.Contains((form.WordId, form.ReadingIndex))) continue;
            validReadings.TryAdd(form.WordId, form.Text);
        }

        var result = new Dictionary<(int, byte), List<string>>();
        foreach (var (sourcePair, confIds) in sourceToConfusable)
        {
            sourceReadings.TryGetValue(sourcePair, out var ownReadings);

            var readings = confIds
                .Where(validReadings.ContainsKey)
                .Select(id => (reading: validReadings[id], rank: freqByWord.GetValueOrDefault(id, int.MaxValue)))
                .Where(x => ownReadings == null || !ownReadings.Contains(x.reading))
                .OrderBy(x => x.rank)
                .Select(x => x.reading)
                .ToList();

            if (readings.Count > 0)
                result[sourcePair] = readings;
        }

        return result;
    }
}
