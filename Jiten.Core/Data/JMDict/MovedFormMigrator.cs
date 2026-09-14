using System.Text;
using Microsoft.EntityFrameworkCore;
using WanaKanaShaapu;

namespace Jiten.Core.Data.JMDict;

/// <summary>Follows a form JMdict moved to another entry: the old entry stops answering lookups for it and its frequency ranks carry over.</summary>
public static class MovedFormMigrator
{
    private const int JmDictRangeEnd = 5000000;

    public sealed record MovedForm(int OldWordId, short OldReadingIndex, string Text, int NewWordId, short NewReadingIndex,
                                   int LookupsRemoved, bool FormRankCopied, bool WordRankCopied);

    public static async Task<List<MovedForm>> Run(IDbContextFactory<JitenDbContext> contextFactory, bool dryRun, string? reportPath = null)
    {
        await using var db = await contextFactory.CreateDbContextAsync();

        var inactive = await db.WordForms.AsNoTracking()
                               .Where(f => !f.IsActiveInLatestSource && f.WordId < JmDictRangeEnd)
                               .Select(f => new { f.WordId, f.ReadingIndex, f.Text })
                               .ToListAsync();
        if (inactive.Count == 0) return [];

        var texts = inactive.Select(f => f.Text).Distinct().ToList();
        var activeByText = (await db.WordForms.AsNoTracking()
                                    .Where(f => f.IsActiveInLatestSource && f.WordId < JmDictRangeEnd && texts.Contains(f.Text))
                                    .Select(f => new { f.WordId, f.ReadingIndex, f.Text })
                                    .ToListAsync())
            .GroupBy(f => f.Text)
            .ToDictionary(g => g.Key, g => g.ToList());

        var moves = new List<(int oldId, short oldRi, string text, int newId, short newRi)>();
        foreach (var f in inactive)
        {
            if (!activeByText.TryGetValue(f.Text, out var targets)) continue;
            // The split-out entry is the newest one carrying the text; the old entry itself never qualifies.
            var target = targets.Where(t => t.WordId != f.WordId).OrderByDescending(t => t.WordId).FirstOrDefault();
            if (target == null) continue;
            moves.Add((f.WordId, f.ReadingIndex, f.Text, target.WordId, target.ReadingIndex));
        }
        if (moves.Count == 0) return [];

        var oldIds = moves.Select(m => m.oldId).Distinct().ToList();
        var newIds = moves.Select(m => m.newId).Distinct().ToList();
        var oldWords = await db.JMDictWords.Include(w => w.Forms).Include(w => w.Lookups)
                               .Where(w => oldIds.Contains(w.WordId)).ToDictionaryAsync(w => w.WordId);
        var newFormTexts = (await db.WordForms.AsNoTracking()
                                    .Where(f => newIds.Contains(f.WordId) && f.IsActiveInLatestSource)
                                    .Select(f => new { f.WordId, f.Text })
                                    .ToListAsync())
            .GroupBy(f => f.WordId).ToDictionary(g => g.Key, g => g.Select(f => f.Text).ToHashSet());

        var formRanks = await db.WordFormFrequencies.Where(f => oldIds.Contains(f.WordId) || newIds.Contains(f.WordId)).ToListAsync();
        var formRanksByType = await db.WordFormFrequenciesByType.Where(f => oldIds.Contains(f.WordId) || newIds.Contains(f.WordId)).ToListAsync();
        var wordRanks = await db.JmDictWordFrequencies.Where(f => oldIds.Contains(f.WordId) || newIds.Contains(f.WordId)).ToListAsync();
        var wordRanksByType = await db.WordFrequenciesByType.Where(f => oldIds.Contains(f.WordId) || newIds.Contains(f.WordId)).ToListAsync();

        var results = new List<MovedForm>();
        foreach (var (oldId, oldRi, text, newId, newRi) in moves)
        {
            var oldWord = oldWords[oldId];
            var movedKeys = LookupKeysFor(text);
            var keptKeys = oldWord.Forms
                                  .Where(f => f.IsActiveInLatestSource)
                                  .SelectMany(f => LookupKeysFor(f.Text))
                                  .ToHashSet();
            var toRemove = oldWord.Lookups.Where(l => movedKeys.Contains(l.LookupKey) && !keptKeys.Contains(l.LookupKey)).ToList();
            if (!dryRun)
            {
                db.Lookups.RemoveRange(toRemove);
                foreach (var l in toRemove) oldWord.Lookups.Remove(l);
            }

            bool formRankCopied = false;
            var oldFormRank = formRanks.FirstOrDefault(f => f.WordId == oldId && f.ReadingIndex == oldRi);
            if (oldFormRank != null && formRanks.All(f => !(f.WordId == newId && f.ReadingIndex == newRi)))
            {
                var copy = new JmDictWordFormFrequency
                {
                    WordId = newId, ReadingIndex = newRi, FrequencyRank = oldFormRank.FrequencyRank,
                    FrequencyPercentage = oldFormRank.FrequencyPercentage, ObservedFrequency = oldFormRank.ObservedFrequency,
                    UsedInMediaAmount = oldFormRank.UsedInMediaAmount,
                };
                formRanks.Add(copy);
                if (!dryRun) db.WordFormFrequencies.Add(copy);
                formRankCopied = true;
            }
            foreach (var oldByType in formRanksByType.Where(f => f.WordId == oldId && f.ReadingIndex == oldRi).ToList())
            {
                if (formRanksByType.Any(f => f.MediaType == oldByType.MediaType && f.WordId == newId && f.ReadingIndex == newRi)) continue;
                var copy = new JmDictWordFormFrequencyByType
                {
                    MediaType = oldByType.MediaType, WordId = newId, ReadingIndex = newRi, FrequencyRank = oldByType.FrequencyRank,
                    FrequencyPercentage = oldByType.FrequencyPercentage, ObservedFrequency = oldByType.ObservedFrequency,
                    UsedInMediaAmount = oldByType.UsedInMediaAmount,
                };
                formRanksByType.Add(copy);
                if (!dryRun) db.WordFormFrequenciesByType.Add(copy);
            }

            // A word-level rank only transfers to a genuine split (every form of the target came from
            // the old entry); a kana form that merely coincides with another entry keeps its own rank.
            var oldTexts = oldWord.Forms.Select(f => f.Text).ToHashSet();
            bool isSplit = newFormTexts.TryGetValue(newId, out var targetTexts) && targetTexts.IsSubsetOf(oldTexts);

            bool wordRankCopied = false;
            var oldWordRank = wordRanks.FirstOrDefault(f => f.WordId == oldId);
            if (isSplit && oldWordRank != null && wordRanks.All(f => f.WordId != newId))
            {
                var copy = new JmDictWordFrequency
                {
                    WordId = newId, FrequencyRank = oldWordRank.FrequencyRank,
                    ObservedFrequency = oldWordRank.ObservedFrequency, UsedInMediaAmount = oldWordRank.UsedInMediaAmount,
                };
                wordRanks.Add(copy);
                if (!dryRun) db.JmDictWordFrequencies.Add(copy);
                wordRankCopied = true;
            }
            foreach (var oldByType in wordRanksByType.Where(f => isSplit && f.WordId == oldId).ToList())
            {
                if (wordRanksByType.Any(f => f.MediaType == oldByType.MediaType && f.WordId == newId)) continue;
                var copy = new JmDictWordFrequencyByType
                {
                    MediaType = oldByType.MediaType, WordId = newId, FrequencyRank = oldByType.FrequencyRank,
                    ObservedFrequency = oldByType.ObservedFrequency, UsedInMediaAmount = oldByType.UsedInMediaAmount,
                };
                wordRanksByType.Add(copy);
                if (!dryRun) db.WordFrequenciesByType.Add(copy);
            }

            results.Add(new MovedForm(oldId, oldRi, text, newId, newRi, toRemove.Count, formRankCopied, wordRankCopied));
        }

        if (!dryRun) await db.SaveChangesAsync();

        if (reportPath != null)
        {
            var sb = new StringBuilder();
            foreach (var r in results)
                sb.AppendLine($"{r.Text}: {r.OldWordId}/{r.OldReadingIndex} -> {r.NewWordId}/{r.NewReadingIndex} lookupsRemoved={r.LookupsRemoved} formRank={(r.FormRankCopied ? "copied" : "kept")} wordRank={(r.WordRankCopied ? "copied" : "kept")}");
            await File.WriteAllTextAsync(reportPath, sb.ToString());
        }

        return results;
    }

    public static void PrintSummary(List<MovedForm> moved, bool dryRun)
    {
        var verb = dryRun ? "Would move" : "Moved";
        Console.WriteLine($"  {verb} {moved.Count} inactive forms to their new entries " +
                          $"({moved.Sum(m => m.LookupsRemoved)} lookups removed, {moved.Count(m => m.FormRankCopied)} form ranks and {moved.Count(m => m.WordRankCopied)} word ranks copied).");
        foreach (var m in moved.Take(30))
            Console.WriteLine($"    {m.Text}: {m.OldWordId}/{m.OldReadingIndex} -> {m.NewWordId}/{m.NewReadingIndex}");
        if (moved.Count > 30) Console.WriteLine($"    ... {moved.Count - 30} more");
    }

    private static HashSet<string> LookupKeysFor(string formText)
    {
        var normalised = formText.Replace("ゎ", "わ").Replace("ヮ", "わ");
        var keys = new HashSet<string>
        {
            WanaKana.ToHiragana(normalised, new DefaultOptions { ConvertLongVowelMark = false }),
            WanaKana.ToHiragana(normalised),
        };
        if (WanaKana.IsKatakana(formText)) keys.Add(formText);
        return keys;
    }
}
