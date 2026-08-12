using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;
using WanaKanaShaapu;
using SudachiRecord = Jiten.Cli.SudachiDictionaryProcessor.SudachiLexiconRecord;
using PruningContext = Jiten.Cli.SudachiDictionaryProcessor.PruningContext;

namespace Jiten.Cli;

public static class WordCompositionImporter
{
    public class ImportReport
    {
        public int ProcessedRows;
        public int ParentJoined;
        public int FullyJoined;
        public int InsertedRows;
        public int SkippedParentMiss;
        public int SkippedComponentMiss;
        public int SkippedNonNumericRef;
        public readonly Dictionary<string, int> UnresolvedComponents = new();

        public override string ToString()
        {
            var top = UnresolvedComponents
                      .OrderByDescending(kv => kv.Value).Take(20)
                      .Select(kv => $"  {kv.Key} × {kv.Value}")
                      .ToList();
            return $@"=== Word Composition Import Report ===
Processed (C-unit rows with split_a): {ProcessedRows}
Parent resolved to JMDict:           {ParentJoined}
All components also resolved:        {FullyJoined}
Composition rows inserted:           {InsertedRows}

Skipped (parent not in JMDict):      {SkippedParentMiss}
Skipped (component not in JMDict):   {SkippedComponentMiss}
Skipped (non-numeric split ref):     {SkippedNonNumericRef}

Top 20 unresolved components:
{string.Join("\n", top)}";
        }
    }

    public static async Task Import(
        IDbContextFactory<JitenDbContext> contextFactory,
        string csvDirectory,
        string splitType,
        bool dryRun)
    {
        var wantC = splitType.Contains('C', StringComparison.OrdinalIgnoreCase);
        var wantB = splitType.Contains('B', StringComparison.OrdinalIgnoreCase);
        if (!wantC && !wantB)
        {
            Console.WriteLine($"Invalid split-type '{splitType}'. Use C, B, or CB.");
            return;
        }

        Console.WriteLine($"Loading SudachiDict CSVs from {csvDirectory}...");
        var records = LoadSudachiRecords(csvDirectory);
        Console.WriteLine($"Loaded {records.Count:N0} Sudachi records.");

        await using var context = await contextFactory.CreateDbContextAsync();

        if (!dryRun)
        {
            Console.WriteLine("Clearing existing WordCompositions data...");
            await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE jmdict.\"WordCompositions\"");
        }

        Console.WriteLine("Building JMDict surface+reading index...");
        var index = await BuildJMDictIndex(context);
        Console.WriteLine($"Indexed {index.Count:N0} (surface, reading) keys.");

        var report = new ImportReport();
        var pending = new List<JmDictWordComposition>();
        var insertedKeys = new HashSet<(int, short, short)>();

        foreach (var (globalId, record) in records)
        {
            var isWanted = (wantC && record.SplitType == "C") || (wantB && record.SplitType == "B");
            if (!isWanted) continue;
            if (string.IsNullOrEmpty(record.SplitInfoAUnit) || record.SplitInfoAUnit == "*") continue;

            var splitIds = ParseSplitIds(record.SplitInfoAUnit);
            if (splitIds == null)
            {
                report.SkippedNonNumericRef++;
                continue;
            }
            if (splitIds.Count < 2) continue;

            report.ProcessedRows++;

            var parent = ResolveRecord(record, index);
            if (parent == null)
            {
                report.SkippedParentMiss++;
                continue;
            }
            report.ParentJoined++;

            var components = new List<(SudachiRecord Rec, (int WordId, short ReadingIndex) Match)>(splitIds.Count);
            bool allResolved = true;
            foreach (var splitId in splitIds)
            {
                if (!records.TryGetValue(splitId, out var componentRecord))
                {
                    allResolved = false;
                    IncrementUnresolved(report, $"[unknown-id:{splitId}]");
                    break;
                }

                var componentMatch = ResolveRecord(componentRecord, index);
                if (componentMatch == null
                    && int.TryParse(componentRecord.DictionaryFormWordId, out var dictFormId)
                    && dictFormId != splitId
                    && records.TryGetValue(dictFormId, out var dictFormRecord))
                {
                    componentMatch = ResolveRecord(dictFormRecord, index);
                }
                if (componentMatch == null)
                {
                    allResolved = false;
                    IncrementUnresolved(report, $"{componentRecord.Surface}({HiraganaReading(componentRecord.Yomi)})");
                    break;
                }

                components.Add((componentRecord, componentMatch.Value));
            }

            if (!allResolved)
            {
                report.SkippedComponentMiss++;
                continue;
            }

            report.FullyJoined++;

            for (short pos = 0; pos < components.Count; pos++)
            {
                var (componentRec, match) = components[pos];
                var key = (parent.Value.WordId, parent.Value.ReadingIndex, pos);
                if (!insertedKeys.Add(key)) continue;

                pending.Add(new JmDictWordComposition
                {
                    WordId = parent.Value.WordId,
                    ReadingIndex = parent.Value.ReadingIndex,
                    Position = pos,
                    ComponentWordId = match.WordId,
                    ComponentReadingIndex = match.ReadingIndex,
                    ComponentSurface = componentRec.Surface
                });
            }

            if (pending.Count >= 50_000)
            {
                if (!dryRun) await FlushBatch(context, pending);
                report.InsertedRows += pending.Count;
                pending.Clear();
            }
        }

        if (!dryRun && pending.Count > 0)
        {
            await FlushBatch(context, pending);
        }
        report.InsertedRows += pending.Count;
        if (dryRun) Console.WriteLine("Dry-run: no rows inserted.");

        Console.WriteLine(report);
    }

    private static Dictionary<int, SudachiRecord> LoadSudachiRecords(string csvDirectory)
    {
        var smallLex = Path.Combine(csvDirectory, "small_lex.csv");
        var coreLex = Path.Combine(csvDirectory, "core_lex.csv");
        var notCoreLex = Path.Combine(csvDirectory, "notcore_lex.csv");
        var filePaths = new[] { smallLex, coreLex, notCoreLex };

        foreach (var p in filePaths)
        {
            if (!File.Exists(p)) throw new FileNotFoundException($"Missing SudachiDict CSV: {p}");
        }

        var ctx = new PruningContext();
        SudachiDictionaryProcessor.LoadAllRecords(filePaths, ctx);
        return ctx.AllRecords;
    }

    private static List<int>? ParseSplitIds(string splitInfo)
    {
        var content = splitInfo.StartsWith('"') ? splitInfo[1..^1] : splitInfo;
        var ids = new List<int>();
        foreach (var part in content.Split('/'))
        {
            if (string.IsNullOrEmpty(part)) continue;
            if (part.StartsWith('U')) return null;
            if (part.Contains(',')) return null;
            if (!int.TryParse(part, out var id)) return null;
            ids.Add(id);
        }
        return ids;
    }

    private record struct JMDictEntry(int WordId, short ReadingIndex, string[] PosTags);

    private static async Task<Dictionary<(string Surface, string Reading), List<JMDictEntry>>> BuildJMDictIndex(
        JitenDbContext context)
    {
        const int batchSize = 10_000;
        int totalWords = await context.JMDictWords.CountAsync();
        int processed = 0;
        var index = new Dictionary<(string, string), List<JMDictEntry>>();

        while (processed < totalWords)
        {
            var batchWordIds = await context.JMDictWords
                .AsNoTracking()
                .OrderBy(w => w.WordId)
                .Skip(processed)
                .Take(batchSize)
                .Select(w => new { w.WordId, w.PartsOfSpeech })
                .ToListAsync();

            if (batchWordIds.Count == 0) break;

            var wordIdList = batchWordIds.Select(w => w.WordId).ToList();
            var forms = await context.WordForms
                .AsNoTracking()
                .Where(f => wordIdList.Contains(f.WordId))
                .ToListAsync();

            var posByWord = batchWordIds.ToDictionary(w => w.WordId, w => w.PartsOfSpeech.ToArray());
            var groupedByWord = forms.GroupBy(f => f.WordId);

            foreach (var group in groupedByWord)
            {
                var kanjiForms = group.Where(f => f.FormType == JmDictFormType.KanjiForm).ToList();
                var kanaForms = group.Where(f => f.FormType == JmDictFormType.KanaForm).ToList();
                var posTags = posByWord.GetValueOrDefault(group.Key) ?? Array.Empty<string>();

                foreach (var kana in kanaForms)
                {
                    var kanaReading = HiraganaReading(kana.Text);

                    foreach (var kanji in kanjiForms)
                    {
                        AddToIndex(index, kanji.Text, kanaReading,
                            new JMDictEntry(group.Key, kanji.ReadingIndex, posTags));
                    }

                    AddToIndex(index, kana.Text, kanaReading,
                        new JMDictEntry(group.Key, kana.ReadingIndex, posTags));
                }
            }

            processed += batchWordIds.Count;
            if (processed % 50_000 < batchSize)
                Console.WriteLine($"Indexed {processed:N0}/{totalWords:N0} words...");
        }

        return index;
    }

    private static void AddToIndex(
        Dictionary<(string, string), List<JMDictEntry>> index,
        string surface, string reading, JMDictEntry entry)
    {
        var key = (surface, reading);
        if (!index.TryGetValue(key, out var list))
        {
            list = new List<JMDictEntry>();
            index[key] = list;
        }
        list.Add(entry);
    }

    private static (int WordId, short ReadingIndex)? ResolveRecord(
        SudachiRecord record,
        Dictionary<(string, string), List<JMDictEntry>> index)
    {
        var surface = record.Surface;
        var reading = HiraganaReading(record.Yomi);
        if (string.IsNullOrEmpty(surface) || string.IsNullOrEmpty(reading)) return null;

        if (!index.TryGetValue((surface, reading), out var candidates) || candidates.Count == 0)
            return null;

        var sudachiPos = PosMapper.FromSudachi(record.Pos1);
        var compatible = candidates
                        .Where(c => PosMapper.IsJmDictCompatibleWithSudachi(c.PosTags, sudachiPos))
                        .ToList();

        var pool = compatible.Count > 0 ? compatible : candidates;
        var best = pool.OrderBy(c => c.ReadingIndex).First();
        return (best.WordId, best.ReadingIndex);
    }

    private static string HiraganaReading(string yomi)
    {
        if (string.IsNullOrEmpty(yomi)) return "";
        return JapaneseTextHelper.ToHiragana(yomi.Replace("ヮ", "わ").Replace("ゎ", "わ"));
    }

    private static void IncrementUnresolved(ImportReport report, string key)
    {
        report.UnresolvedComponents[key] = report.UnresolvedComponents.GetValueOrDefault(key) + 1;
    }

    private static async Task FlushBatch(JitenDbContext context, List<JmDictWordComposition> pending)
    {
        const int subBatch = 5_000;
        for (int i = 0; i < pending.Count; i += subBatch)
        {
            var slice = pending.Skip(i).Take(subBatch).ToList();
            context.WordCompositions.AddRange(slice);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }
    }
}
