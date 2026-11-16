using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Cli.Commands;

public partial class TatoebaBenchmarkCommands(CliContext context)
{
    private const int FirstNameEntryId = 5_000_000;
    private const int ExamplesPerConfusion = 3;

    /// <summary>One word of a Tatoeba index line: headword(reading or #seq)[sense]{surface}~, where ~ marks a human-checked word.</summary>
    [GeneratedRegex(@"^(?<head>[^(\[{~|]+)(?:\|\d+)?(?:\((?<reading>[^)]*)\))?(?:\[\d+\])*(?:\{(?<surface>[^}]*)\})?(?<checked>~)?$")]
    private static partial Regex IndexItemRegex();

    private sealed record IndexItem(string Headword, string? Reading, int? Seq, string Surface, bool Checked);

    private sealed record ParsedToken(string Text, int WordId, int Start, int End, string? SudachiReading);

    private enum Outcome
    {
        Match,
        MatchDifferentSpan,
        WrongWord,
        ParserCompound,
        ParserSplit,
        BoundaryMismatch,
        Missing
    }

    private sealed class Confusion
    {
        public required string Kind { get; init; }
        public required string Headword { get; init; }
        public string? Reading { get; init; }
        public required int[] ExpectedIds { get; init; }
        public required int[] GotIds { get; init; }
        public required string GotTokens { get; init; }
        public int Count;
        public int CheckedCount;
        public Dictionary<string, int> SudachiReadings { get; } = [];
        public List<ConfusionExample> Examples { get; } = [];
    }

    private sealed record ConfusionExample(int SentenceId, string Sentence, string Surface);

    private sealed class Tally
    {
        public int Scored, Match, MatchDifferentSpan, WrongWord, ParserCompound, ParserSplit, BoundaryMismatch, Missing;

        public void Add(Outcome outcome)
        {
            Scored++;
            switch (outcome)
            {
                case Outcome.Match: Match++; break;
                case Outcome.MatchDifferentSpan: MatchDifferentSpan++; break;
                case Outcome.WrongWord: WrongWord++; break;
                case Outcome.ParserCompound: ParserCompound++; break;
                case Outcome.ParserSplit: ParserSplit++; break;
                case Outcome.BoundaryMismatch: BoundaryMismatch++; break;
                case Outcome.Missing: Missing++; break;
            }
        }

        public object ToReport() => new
        {
            Scored,
            Match,
            MatchDifferentSpan,
            WrongWord,
            ParserCompound,
            ParserSplit,
            BoundaryMismatch,
            Missing,
            StrictAgreement = Scored > 0 ? Math.Round((double)Match / Scored * 100, 2) : 0,
            LemmaAgreement = Scored > 0 ? Math.Round((double)(Match + MatchDifferentSpan) / Scored * 100, 2) : 0,
            HardDisagreement = Scored > 0 ? Math.Round((double)(WrongWord + BoundaryMismatch + Missing) / Scored * 100, 2) : 0
        };
    }

    public async Task Run(CliOptions options)
    {
        var folder = options.TatoebaBenchmark!;
        var indexPath = Path.Combine(folder, "jpn_indices.csv");
        var sentencePath = Path.Combine(folder, "jpn_sentences.tsv");
        if (!File.Exists(indexPath) || !File.Exists(sentencePath))
        {
            Console.WriteLine($"Expected jpn_indices.csv and jpn_sentences.tsv in {folder}");
            return;
        }

        var sentences = new Dictionary<int, string>();
        foreach (var line in File.ReadLines(sentencePath))
        {
            var parts = line.Split('\t');
            if (parts.Length >= 3 && int.TryParse(parts[0], out var id))
                sentences[id] = parts[2];
        }

        var entries = new List<(int SentenceId, string Sentence, List<IndexItem> Items)>();
        int unparsedItems = 0;
        foreach (var line in File.ReadLines(indexPath))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3 || !int.TryParse(parts[0], out var id) || !sentences.TryGetValue(id, out var sentence))
                continue;

            var items = new List<IndexItem>();
            foreach (var raw in parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var m = IndexItemRegex().Match(raw);
                if (!m.Success)
                {
                    unparsedItems++;
                    continue;
                }

                var head = m.Groups["head"].Value;
                var reading = m.Groups["reading"].Success ? m.Groups["reading"].Value : null;
                int? seq = null;
                if (reading is { Length: > 1 } && reading[0] == '#' && int.TryParse(reading.AsSpan(1), out var parsedSeq))
                {
                    seq = parsedSeq;
                    reading = null;
                }

                items.Add(new IndexItem(head, reading, seq, m.Groups["surface"].Success ? m.Groups["surface"].Value : head,
                                        m.Groups["checked"].Success));
            }

            if (items.Count > 0)
                entries.Add((id, sentence, items));
            if (options.TatoebaLimit > 0 && entries.Count >= options.TatoebaLimit)
                break;
        }

        Console.WriteLine($"Loaded {entries.Count:N0} indexed sentences ({unparsedItems:N0} unparseable items skipped)");

        var formIds = await LoadFormIds(entries.SelectMany(e => e.Items).SelectMany(i => i.Reading != null
                                                                                         ? new[] { i.Headword, i.Reading }
                                                                                         : [i.Headword]).ToHashSet());

        var all = new Tally();
        var checkedOnly = new Tally();
        var confusions = new ConcurrentDictionary<string, Confusion>();
        int unresolvedItems = 0, unalignedItems = 0, unlocatedTokens = 0, done = 0;
        var sw = Stopwatch.StartNew();

        await Parallel.ForEachAsync(entries, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (entry, _) =>
        {
            var words = await Jiten.Parser.Parser.ParseText(context.ContextFactory, entry.Sentence);
            var tokens = LocateTokens(entry.Sentence, words);
            Interlocked.Add(ref unlocatedTokens, words.Count - tokens.Count);

            int cursor = 0;
            foreach (var item in entry.Items)
            {
                int start = entry.Sentence.IndexOf(item.Surface, cursor, StringComparison.Ordinal);
                if (start < 0)
                {
                    Interlocked.Increment(ref unalignedItems);
                    continue;
                }

                int end = start + item.Surface.Length;
                cursor = end;

                var expected = ResolveExpected(item, formIds);
                if (expected.Length == 0)
                {
                    Interlocked.Increment(ref unresolvedItems);
                    continue;
                }

                var overlapping = tokens.Where(t => t.Start < end && t.End > start).ToList();
                var exact = overlapping.FirstOrDefault(t => t.Start == start && t.End == end);
                var outcome = Classify(start, end, expected, overlapping, exact);

                lock (all)
                {
                    all.Add(outcome);
                    if (item.Checked) checkedOnly.Add(outcome);
                }

                if (outcome is Outcome.Match or Outcome.MatchDifferentSpan)
                    continue;

                var gotTokens = string.Join(" | ", overlapping.Select(t => $"{t.Text}[{t.WordId}]"));
                var key = $"{outcome}\t{item.Headword}\t{item.Reading}\t{item.Seq}\t{string.Join(',', overlapping.Select(t => t.WordId))}";
                var confusion = confusions.GetOrAdd(key, _ => new Confusion
                {
                    Kind = outcome.ToString(),
                    Headword = item.Headword,
                    Reading = item.Reading,
                    ExpectedIds = expected,
                    GotIds = overlapping.Select(t => t.WordId).Distinct().ToArray(),
                    GotTokens = gotTokens
                });
                lock (confusion)
                {
                    confusion.Count++;
                    if (item.Checked) confusion.CheckedCount++;
                    if (exact?.SudachiReading is { Length: > 0 } sudachiReading)
                        confusion.SudachiReadings[sudachiReading] = confusion.SudachiReadings.GetValueOrDefault(sudachiReading) + 1;
                    if (confusion.Examples.Count < ExamplesPerConfusion)
                        confusion.Examples.Add(new ConfusionExample(entry.SentenceId, entry.Sentence, item.Surface));
                }
            }

            int n = Interlocked.Increment(ref done);
            if (n % 10_000 == 0)
                Console.WriteLine($"  {n:N0}/{entries.Count:N0} sentences ({sw.Elapsed.TotalSeconds:N0}s)");
        });

        var ranked = confusions.Values.OrderByDescending(c => c.Count).ThenBy(c => c.Headword, StringComparer.Ordinal).ToList();
        var glosses = await LoadGlosses(ranked.SelectMany(c => c.ExpectedIds.Take(4).Concat(c.GotIds)).ToHashSet());

        var report = new
        {
            Sentences = entries.Count,
            UnparsedItems = unparsedItems,
            UnalignedItems = unalignedItems,
            UnresolvedItems = unresolvedItems,
            UnlocatedParserTokens = unlocatedTokens,
            All = all.ToReport(),
            Checked = checkedOnly.ToReport(),
            DistinctConfusions = ranked.Count,
            Confusions = ranked.Select(c => new
            {
                c.Kind,
                c.Headword,
                c.Reading,
                c.Count,
                c.CheckedCount,
                Expected = c.ExpectedIds.Take(4).Select(id => Describe(id, glosses)),
                ExpectedCandidateCount = c.ExpectedIds.Length,
                c.GotTokens,
                Got = c.GotIds.Select(id => Describe(id, glosses)),
                c.SudachiReadings,
                c.Examples
            })
        };

        Console.WriteLine();
        Console.WriteLine($"Sentences: {entries.Count:N0}, scored items: {all.Scored:N0} " +
                          $"(unaligned {unalignedItems:N0}, unresolved headword {unresolvedItems:N0})");
        PrintTally("All items", all);
        PrintTally("Checked (~) items", checkedOnly);
        Console.WriteLine($"Distinct disagreements: {ranked.Count:N0}");

        if (!string.IsNullOrEmpty(options.ParseTestOutput))
        {
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
            await File.WriteAllTextAsync(options.ParseTestOutput, JsonSerializer.Serialize(report, jsonOptions));
            Console.WriteLine($"Report written to {options.ParseTestOutput}");
        }
    }

    /// <summary>A longer parser token (では for で + は) or an exact tiling (の + です for のです) is a granularity difference, not a boundary error.</summary>
    private static Outcome Classify(int start, int end, int[] expected, List<ParsedToken> overlapping, ParsedToken? exact)
    {
        if (exact != null)
            return expected.Contains(exact.WordId) ? Outcome.Match : Outcome.WrongWord;
        if (overlapping.Count == 0)
            return Outcome.Missing;
        if (overlapping.Any(t => expected.Contains(t.WordId)))
            return Outcome.MatchDifferentSpan;
        if (overlapping.Count == 1 && overlapping[0].Start <= start && overlapping[0].End >= end)
            return Outcome.ParserCompound;

        bool tiles = overlapping[0].Start == start && overlapping[^1].End == end;
        for (int i = 1; tiles && i < overlapping.Count; i++)
            tiles = overlapping[i].Start == overlapping[i - 1].End;
        return tiles ? Outcome.ParserSplit : Outcome.BoundaryMismatch;
    }

    private static void PrintTally(string label, Tally t)
    {
        if (t.Scored == 0) return;
        Console.WriteLine($"{label}: {t.Scored:N0}");
        Console.WriteLine($"  same word, same span:      {t.Match,9:N0} ({(double)t.Match / t.Scored:P2})");
        Console.WriteLine($"  same word, different span: {t.MatchDifferentSpan,9:N0} ({(double)t.MatchDifferentSpan / t.Scored:P2})");
        Console.WriteLine($"  different word, same span: {t.WrongWord,9:N0} ({(double)t.WrongWord / t.Scored:P2})");
        Console.WriteLine($"  parser has a longer token: {t.ParserCompound,9:N0} ({(double)t.ParserCompound / t.Scored:P2})");
        Console.WriteLine($"  parser splits the item:    {t.ParserSplit,9:N0} ({(double)t.ParserSplit / t.Scored:P2})");
        Console.WriteLine($"  boundaries cross:          {t.BoundaryMismatch,9:N0} ({(double)t.BoundaryMismatch / t.Scored:P2})");
        Console.WriteLine($"  no parser token:           {t.Missing,9:N0} ({(double)t.Missing / t.Scored:P2})");
    }

    private static List<ParsedToken> LocateTokens(string sentence, List<DeckWord> words)
    {
        var tokens = new List<ParsedToken>(words.Count);
        int cursor = 0;
        foreach (var word in words)
        {
            int start = string.IsNullOrEmpty(word.OriginalText) ? -1 : sentence.IndexOf(word.OriginalText, cursor, StringComparison.Ordinal);
            if (start < 0)
                continue;

            int end = start + word.OriginalText.Length;
            tokens.Add(new ParsedToken(word.OriginalText, word.WordId, start, end, word.SudachiReading));
            cursor = end;
        }

        return tokens;
    }

    private static int[] ResolveExpected(IndexItem item, Dictionary<string, HashSet<int>> formIds)
    {
        if (item.Seq is { } seq)
            return [seq];
        if (!formIds.TryGetValue(item.Headword, out var byHeadword))
            return [];
        if (item.Reading == null)
            return [..byHeadword];
        return formIds.TryGetValue(item.Reading, out var byReading) ? byHeadword.Intersect(byReading).ToArray() : [];
    }

    private async Task<Dictionary<string, HashSet<int>>> LoadFormIds(HashSet<string> texts)
    {
        await using var db = await context.ContextFactory.CreateDbContextAsync();
        var result = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var chunk in texts.Chunk(5000))
        {
            var rows = await db.WordForms.AsNoTracking()
                               .Where(f => chunk.Contains(f.Text) && f.WordId < FirstNameEntryId)
                               .Select(f => new { f.Text, f.WordId })
                               .ToListAsync();
            foreach (var row in rows)
            {
                if (!result.TryGetValue(row.Text, out var ids))
                    result[row.Text] = ids = [];
                ids.Add(row.WordId);
            }
        }

        return result;
    }

    private async Task<Dictionary<int, (string Forms, string Gloss)>> LoadGlosses(HashSet<int> wordIds)
    {
        await using var db = await context.ContextFactory.CreateDbContextAsync();
        var result = new Dictionary<int, (string, string)>();
        foreach (var chunk in wordIds.Chunk(5000))
        {
            var forms = await db.WordForms.AsNoTracking()
                                .Where(f => chunk.Contains(f.WordId))
                                .Select(f => new { f.WordId, f.ReadingIndex, f.Text })
                                .ToListAsync();
            var definitions = await db.Definitions.AsNoTracking()
                                      .Where(d => chunk.Contains(d.WordId))
                                      .Select(d => new { d.WordId, d.SenseIndex, d.EnglishMeanings })
                                      .ToListAsync();
            var glossByWord = definitions.GroupBy(d => d.WordId)
                                         .ToDictionary(g => g.Key, g => string.Join("; ", g.OrderBy(d => d.SenseIndex).Take(2)
                                                                                           .Select(d => string.Join(", ", d.EnglishMeanings.Take(3)))));
            foreach (var group in forms.GroupBy(f => f.WordId))
                result[group.Key] = (string.Join("/", group.OrderBy(f => f.ReadingIndex).Take(3).Select(f => f.Text)),
                                     glossByWord.GetValueOrDefault(group.Key, ""));
        }

        return result;
    }

    private static object Describe(int wordId, Dictionary<int, (string Forms, string Gloss)> glosses) =>
        glosses.TryGetValue(wordId, out var g)
            ? new { WordId = wordId, g.Forms, g.Gloss }
            : new { WordId = wordId, Forms = "?", Gloss = "" };
}
