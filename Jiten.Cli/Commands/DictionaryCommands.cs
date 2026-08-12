using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;
using WanaKanaShaapu;

namespace Jiten.Cli.Commands;

public class DictionaryCommands(CliContext context)
{
    public async Task AddWordsToUserDictionary(string existingWords, string userDicPath)
    {
        var excludeList = await File.ReadAllLinesAsync(existingWords);
        var excludeSet = new HashSet<string>(excludeList, StringComparer.Ordinal);

        var existingXmlFirstTokens = new HashSet<string>(StringComparer.Ordinal);
        if (File.Exists(userDicPath))
        {
            var xmlExistingLines = await File.ReadAllLinesAsync(userDicPath);
            foreach (var l in xmlExistingLines)
            {
                if (string.IsNullOrWhiteSpace(l)) continue;
                var surface = l.Split(',', 2)[0].Trim();
                if (!string.IsNullOrEmpty(surface))
                    existingXmlFirstTokens.Add(surface);
            }
        }

        excludeSet.UnionWith(existingXmlFirstTokens);

        await using var context1 = await context.ContextFactory.CreateDbContextAsync();

        var lookups = context1.Lookups.AsNoTracking().ToList();
        var words = context1.JMDictWords.AsNoTracking().ToList();

        var allReadings = context1.WordForms.AsNoTracking().Select(wf => wf.Text).Distinct().ToList();
        var wordsToAdd = allReadings.Where(r => !excludeSet.Contains(r));

        var lookupDict = lookups
                         .GroupBy(l => l.LookupKey)
                         .ToDictionary(g => g.Key, g => g.First());

        var wordDict = words
                       .GroupBy(w => w.WordId)
                       .ToDictionary(g => g.Key, g => g.First());

        const int batchSize = 10000;
        var buffer = new List<string>(batchSize);
        int addedCount = 0;

        foreach (var reading in wordsToAdd)
        {
            var readingInHiragana = JapaneseTextHelper.ToHiragana(reading);
            if (!lookupDict.TryGetValue(readingInHiragana, out var lookup))
                continue;

            if (!wordDict.TryGetValue(lookup.WordId, out var word))
                continue;

            var wordForms = await context1.WordForms.AsNoTracking()
                .Where(wf => wf.WordId == word.WordId)
                .OrderBy(wf => wf.ReadingIndex)
                .ToListAsync();
            var kanaForm = wordForms.FirstOrDefault(wf => wf.FormType == JmDictFormType.KanaForm);
            var kanas = kanaForm?.Text ?? reading;

            var pos = word.PartsOfSpeech.Select(p => p.ToPartOfSpeech()).ToList();

            string posKanji = "NULL";
            if (pos.Contains(PartOfSpeech.Expression))
                posKanji = "表現";
            else if (pos.Contains(PartOfSpeech.Adverb))
                posKanji = "副詞";
            else if (pos.Contains(PartOfSpeech.Conjunction))
                posKanji = "接続詞";
            else if (pos.Contains(PartOfSpeech.Auxiliary))
                posKanji = "助動詞";
            else if (pos.Contains(PartOfSpeech.Pronoun))
                posKanji = "代名詞";
            else if (pos.Contains(PartOfSpeech.Noun))
            {
                if (reading == "ていい" || reading == "からな")
                    continue;

                posKanji = "名詞";
            }
            else if (pos.Contains(PartOfSpeech.Particle))
                posKanji = "助詞";
            else if (pos.Contains(PartOfSpeech.NaAdjective))
                posKanji = "形状詞";
            else if (pos.Contains(PartOfSpeech.IAdjective))
                posKanji = "形容詞";
            else if (pos.Contains(PartOfSpeech.Verb))
                posKanji = "動詞";
            else if (pos.Contains(PartOfSpeech.NominalAdjective))
                posKanji = "形動";
            else if (pos.Contains(PartOfSpeech.Interjection))
                posKanji = "感動詞";
            else if (pos.Contains(PartOfSpeech.Numeral))
                posKanji = "数詞";
            else if (pos.Contains(PartOfSpeech.Suffix))
                posKanji = "接尾辞";
            else if (pos.Contains(PartOfSpeech.Counter))
                posKanji = "助数詞";
            else if (pos.Contains(PartOfSpeech.AdverbTo))
                posKanji = "副詞的と";
            else if (pos.Contains(PartOfSpeech.NounSuffix))
                posKanji = "名詞接尾辞";
            else if (pos.Contains(PartOfSpeech.PrenounAdjectival))
                posKanji = "連体詞";
            else if (pos.Contains(PartOfSpeech.Name))
            {
                if (WanaKana.IsHiragana(reading) || reading == "イーノ" || reading == "ドーダ" || reading == "コトカ")
                    continue;
                posKanji = "名";
            }
            else if (pos.Contains(PartOfSpeech.Prefix))
                posKanji = "接頭詞";


            var xmlLine = $"{reading},5146,5146,5000,{reading},{posKanji},普通名詞,一般,*,*,*,{WanaKana.ToKatakana(kanas)},{reading},*,*,*,*,*";
            buffer.Add(xmlLine);
            existingXmlFirstTokens.Add(reading);
            addedCount++;

            if (buffer.Count >= batchSize)
            {
                await File.AppendAllLinesAsync(userDicPath, buffer);
                Console.WriteLine($"Wrote {buffer.Count} lines to file");
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            await File.AppendAllLinesAsync(userDicPath, buffer);
            Console.WriteLine($"Wrote {buffer.Count} lines to file");
            buffer.Clear();
        }

        Console.WriteLine($"Added {addedCount} entries.");
    }

    public async Task PruneSudachiCsvFiles(string folderPath)
    {
        await using var context1 = await context.ContextFactory.CreateDbContextAsync();
        var allReadings = context1.WordForms
                                 .AsNoTracking()
                                 .Select(wf => wf.Text)
                                 .ToHashSet();

        Console.WriteLine($"Loaded {allReadings.Count} readings.");

        await SudachiDictionaryProcessor.PruneAndFixSudachiCsvFiles(folderPath, allReadings);

        Console.WriteLine($"--- Pruning and fixing complete. ---");
    }
}
