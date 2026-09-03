using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Jiten.Core.Data;
using Jiten.Parser;
using Jiten.Parser.Diagnostics;

namespace Jiten.Cli.Commands;

public class BenchmarkCommands(CliContext context)
{
    public async Task RunBenchmark(CliOptions options)
    {
        if (string.IsNullOrEmpty(options.Benchmark))
            return;

        if (!Directory.Exists(options.Benchmark))
        {
            Console.WriteLine($"Directory not found: {options.Benchmark}");
            return;
        }

        var txtFiles = Directory.GetFiles(options.Benchmark, "*.txt")
            .OrderBy(f => f)
            .ToList();

        if (txtFiles.Count == 0)
        {
            Console.WriteLine($"No .txt files found in: {options.Benchmark}");
            return;
        }

        if (!Enum.TryParse(options.DeckType ?? "Novel", out MediaType deckType))
        {
            deckType = MediaType.Novel;
        }

        Console.WriteLine($"Found {txtFiles.Count} txt files");
        Console.WriteLine($"Media type: {deckType}");
        Console.WriteLine($"Warmup: {options.BenchmarkWarmup}");
        Console.WriteLine();

        if (options.BenchmarkWarmup)
        {
            Console.WriteLine("Running warmup parse...");
            var warmupText = "これはテストです。日本語のテキストを解析しています。";
            await Jiten.Parser.Parser.ParseTextToDeck(context.ContextFactory, warmupText, false, false, deckType);
            Console.WriteLine("Warmup complete.");
            Console.WriteLine();
        }

        for (int pass = 1; pass < options.BenchmarkPasses; pass++)
        {
            var passSw = Stopwatch.StartNew();
            foreach (var filePath in txtFiles)
                await Jiten.Parser.Parser.ParseTextToDeck(context.ContextFactory, await File.ReadAllTextAsync(filePath), false, false, deckType);
            Console.WriteLine($"Pass {pass}/{options.BenchmarkPasses} (not reported): {passSw.ElapsedMilliseconds:N0} ms");
        }
        if (options.BenchmarkPasses > 1) Console.WriteLine();

        var gcPauseBefore = GC.GetTotalPauseDuration();
        var gcAllocBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gc0Before = GC.CollectionCount(0), gc1Before = GC.CollectionCount(1), gc2Before = GC.CollectionCount(2);
        ParserCounters.Reset();
        ParserCounters.SectionTiming = options.BenchmarkSections;
        var results = new List<BenchmarkFileResult>();
        var dump = options.BenchmarkDump != null ? new System.Text.StringBuilder() : null;
        var aggregateTimings = new BenchmarkTimings();
        var stopwatch = new Stopwatch();

        Console.WriteLine("=== Benchmark Results ===");
        Console.WriteLine();
        Console.WriteLine("File Results:");

        for (int i = 0; i < txtFiles.Count; i++)
        {
            var filePath = txtFiles[i];
            var fileName = Path.GetFileName(filePath);
            var content = await File.ReadAllTextAsync(filePath);
            var characterCount = content.Length;

            var fileTimings = new BenchmarkTimings();
            stopwatch.Restart();
            var deck = await Jiten.Parser.Parser.ParseTextToDeck(context.ContextFactory, content, false, false, deckType, timings: fileTimings);
            stopwatch.Stop();

            var elapsedMs = stopwatch.ElapsedMilliseconds;
            var wordCount = deck.DeckWords.Count;
            var charsPerSecond = elapsedMs > 0 ? (double)characterCount / elapsedMs * 1000 : 0;

            aggregateTimings.Accumulate(fileTimings);
            if (dump != null) AppendDeckSignature(dump, fileName, deck);

            results.Add(new BenchmarkFileResult
            {
                FileName = fileName,
                CharacterCount = characterCount,
                WordCount = wordCount,
                ElapsedMs = elapsedMs,
                CharsPerSecond = charsPerSecond,
                Timings = fileTimings
            });

            Console.WriteLine($"  {i + 1,3}. {fileName} - {characterCount:N0} chars, {wordCount:N0} words, {elapsedMs:N0} ms ({charsPerSecond:N0} chars/sec)");
            Console.WriteLine($"       MorphAnalysis: {fileTimings.MorphologicalAnalysisMs:N1}ms [TextPrep={fileTimings.TextPreprocessMs:N1} FFI={fileTimings.SudachiFFIMs:N1} Parse={fileTimings.TokenParsingMs:N1} Offsets={fileTimings.OffsetRecoveryMs:N1} Pipeline={fileTimings.PipelineMs:N1} SentSplit={fileTimings.SentenceSplitMs:N1}]");
            Console.WriteLine($"       Preprocess: {fileTimings.PreprocessingMs:N1}ms | Deconj/Lookup: {fileTimings.DeconjugationLookupMs:N1}ms | Reseg: {fileTimings.ResegmentationMs:N1}ms | AdjScore: {fileTimings.AdjacentScoringMs:N1}ms | Stats: {fileTimings.StatsBuildMs:N1}ms");
        }

        Console.WriteLine();
        Console.WriteLine("Summary:");

        var totalFiles = results.Count;
        var totalCharacters = results.Sum(r => r.CharacterCount);
        var totalWords = results.Sum(r => r.WordCount);
        var totalElapsedMs = results.Sum(r => r.ElapsedMs);
        var averageTimePerFileMs = totalFiles > 0 ? (double)totalElapsedMs / totalFiles : 0;
        var averageCharsPerSecond = totalElapsedMs > 0 ? (double)totalCharacters / totalElapsedMs * 1000 : 0;
        var minTimeMs = results.Count > 0 ? results.Min(r => r.ElapsedMs) : 0;
        var maxTimeMs = results.Count > 0 ? results.Max(r => r.ElapsedMs) : 0;

        Console.WriteLine($"  Files:           {totalFiles}");
        Console.WriteLine($"  Total chars:     {totalCharacters:N0}");
        Console.WriteLine($"  Total words:     {totalWords:N0}");
        Console.WriteLine($"  Total time:      {totalElapsedMs:N0} ms");
        Console.WriteLine($"  Avg time/file:   {averageTimePerFileMs:N1} ms");
        Console.WriteLine($"  Avg chars/sec:   {averageCharsPerSecond:N0}");
        Console.WriteLine($"  Min time:        {minTimeMs:N0} ms");
        Console.WriteLine($"  Max time:        {maxTimeMs:N0} ms");
        Console.WriteLine();
        Console.WriteLine("Time Breakdown (aggregate):");
        var t = aggregateTimings;
        Console.WriteLine($"  Morph. Analysis: {t.MorphologicalAnalysisMs:N1} ms ({Pct(t.MorphologicalAnalysisMs, t.TotalMs)})");
        Console.WriteLine($"    Text Preproc:  {t.TextPreprocessMs:N1} ms ({Pct(t.TextPreprocessMs, t.TotalMs)})");
        Console.WriteLine($"    Sudachi FFI:   {t.SudachiFFIMs:N1} ms ({Pct(t.SudachiFFIMs, t.TotalMs)})");
        Console.WriteLine($"    Token Parsing: {t.TokenParsingMs:N1} ms ({Pct(t.TokenParsingMs, t.TotalMs)})");
        Console.WriteLine($"    Offsets:       {t.OffsetRecoveryMs:N1} ms ({Pct(t.OffsetRecoveryMs, t.TotalMs)})");
        Console.WriteLine($"    Pipeline:      {t.PipelineMs:N1} ms ({Pct(t.PipelineMs, t.TotalMs)})");
        Console.WriteLine($"    Sent. Split:   {t.SentenceSplitMs:N1} ms ({Pct(t.SentenceSplitMs, t.TotalMs)})");
        Console.WriteLine($"  Preprocessing:   {t.PreprocessingMs:N1} ms ({Pct(t.PreprocessingMs, t.TotalMs)})");
        Console.WriteLine($"    NounCompounds: {t.PrepNounCompoundsMs:N1} ms ({Pct(t.PrepNounCompoundsMs, t.TotalMs)})");
        Console.WriteLine($"    Compounds:     {t.PrepCompoundsMs:N1} ms ({Pct(t.PrepCompoundsMs, t.TotalMs)})");
        Console.WriteLine($"    Expressions:   {t.PrepExpressionsMs:N1} ms ({Pct(t.PrepExpressionsMs, t.TotalMs)})");
        Console.WriteLine($"    Grammar:       {t.PrepGrammarMs:N1} ms ({Pct(t.PrepGrammarMs, t.TotalMs)})");
        Console.WriteLine($"    Reseg:         {t.PrepResegmentationMs:N1} ms ({Pct(t.PrepResegmentationMs, t.TotalMs)})");
        Console.WriteLine($"    Other:         {t.PrepOtherMs:N1} ms ({Pct(t.PrepOtherMs, t.TotalMs)})");
        Console.WriteLine($"  Deconj/Lookup:   {t.DeconjugationLookupMs:N1} ms ({Pct(t.DeconjugationLookupMs, t.TotalMs)})");
        Console.WriteLine($"  Resegmentation:  {t.ResegmentationMs:N1} ms ({Pct(t.ResegmentationMs, t.TotalMs)})");
        Console.WriteLine($"  Adj. Scoring:    {t.AdjacentScoringMs:N1} ms ({Pct(t.AdjacentScoringMs, t.TotalMs)})");
        Console.WriteLine($"  Stats Build:     {t.StatsBuildMs:N1} ms ({Pct(t.StatsBuildMs, t.TotalMs)})");
        Console.WriteLine($"  Tracked Total:   {t.TotalMs:N1} ms");

        var deconjStats = Deconjugator.Instance.GetCacheStats();
        Console.WriteLine();
        Console.WriteLine("Deconjugator Cache:");
        Console.WriteLine($"    Hits:       {deconjStats.Hits:N0}");
        Console.WriteLine($"    Misses:     {deconjStats.Misses:N0}");
        Console.WriteLine($"    Hit Rate:   {(deconjStats.Hits + deconjStats.Misses > 0 ? (double)deconjStats.Hits / (deconjStats.Hits + deconjStats.Misses) * 100 : 0):F1}%");
        Console.WriteLine($"    BFS calls:  {deconjStats.BfsCalls:N0}");
        Console.WriteLine($"    BFS time:   {deconjStats.BfsTimeMs:N1} ms ({deconjStats.BfsTimeMs / t.TotalMs * 100:F1}% of total)");
        Console.WriteLine($"    Avg BFS:    {(deconjStats.BfsCalls > 0 ? deconjStats.BfsTimeMs / deconjStats.BfsCalls : 0):F3} ms/call");
        Console.WriteLine($"    Entries:    {deconjStats.Count:N0} / {deconjStats.MaxEntries:N0}");

        Console.WriteLine();
        Console.WriteLine("GC:");
        Console.WriteLine($"    Mode:       {(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")}, latency {System.Runtime.GCSettings.LatencyMode}");
        Console.WriteLine($"    Pause:      {(GC.GetTotalPauseDuration() - gcPauseBefore).TotalMilliseconds:N0} ms");
        Console.WriteLine($"    Allocated:  {(GC.GetTotalAllocatedBytes(precise: true) - gcAllocBefore) / (1024.0 * 1024.0):N0} MB");
        Console.WriteLine($"    Gen0/1/2:   {GC.CollectionCount(0) - gc0Before} / {GC.CollectionCount(1) - gc1Before} / {GC.CollectionCount(2) - gc2Before}");
        Console.WriteLine();
        Console.WriteLine("Parser Counters:");
        Console.Write(ParserCounters.Dump());

        if (t.PipelineStageMs.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Pipeline Stage Breakdown:");
            foreach (var (stage, ms) in t.PipelineStageMs.OrderByDescending(kv => kv.Value))
            {
                Console.WriteLine($"    {stage,-35} {ms,10:N1} ms ({Pct(ms, t.PipelineMs)})");
            }
        }

        if (dump != null)
        {
            await File.WriteAllTextAsync(options.BenchmarkDump!, dump.ToString());
            Console.WriteLine($"Deck signatures written to: {options.BenchmarkDump}");
        }

        if (!string.IsNullOrEmpty(options.Output))
        {
            var benchmarkOutput = new BenchmarkOutput
            {
                Files = results,
                Summary = new BenchmarkSummary
                {
                    TotalFiles = totalFiles,
                    TotalCharacters = totalCharacters,
                    TotalWords = totalWords,
                    TotalElapsedMs = totalElapsedMs,
                    AverageTimePerFileMs = averageTimePerFileMs,
                    AverageCharsPerSecond = averageCharsPerSecond,
                    MinTimeMs = minTimeMs,
                    MaxTimeMs = maxTimeMs,
                    AggregateTimings = aggregateTimings
                }
            };

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            await File.WriteAllTextAsync(options.Output, JsonSerializer.Serialize(benchmarkOutput, jsonOptions));
            Console.WriteLine();
            Console.WriteLine($"Results written to: {options.Output}");
        }
    }

    private static void AppendDeckSignature(System.Text.StringBuilder sb, string fileName, Deck deck)
    {
        sb.Append("## ").Append(fileName).Append('\n');
        sb.Append($"chars={deck.CharacterCount} words={deck.WordCount} unique={deck.UniqueWordCount} once={deck.UniqueWordUsedOnceCount} kanji={deck.UniqueKanjiCount} kanjiOnce={deck.UniqueKanjiUsedOnceCount} sentences={deck.SentenceCount} dialogue={deck.DialoguePercentage:R}\n");
        foreach (var w in deck.DeckWords)
        {
            sb.Append(w.WordId).Append(':').Append(w.ReadingIndex).Append(':').Append(w.Occurrences)
              .Append(':').Append(w.OriginalText).Append(':').Append((int)w.Origin)
              .Append(':').Append(string.Join(",", w.PartsOfSpeech.Select(p => (int)p)))
              .Append(':').Append(w.SudachiReading).Append(':').Append((int)w.SudachiPartOfSpeech)
              .Append(':').Append(w.CachedMargin?.ToString() ?? "-").Append(':').Append(string.Join(",", w.Conjugations)).Append('\n');
        }
        if (deck.ExampleSentences != null)
        {
            foreach (var s in deck.ExampleSentences)
            {
                sb.Append("S ").Append(s.Position).Append(':').Append(s.Difficulty.ToString("R")).Append(':').Append(s.Text).Append(" | ");
                foreach (var sw in s.Words)
                    sb.Append(sw.WordId).Append('/').Append(sw.ReadingIndex).Append('/').Append(sw.Position).Append('/').Append(sw.Length).Append(' ');
                sb.Append('\n');
            }
        }
    }

    private static string Pct(double part, double total) =>
        total > 0 ? $"{part / total * 100:N1}%" : "0.0%";

    private class BenchmarkFileResult
    {
        public string FileName { get; set; } = "";
        public int CharacterCount { get; set; }
        public int WordCount { get; set; }
        public long ElapsedMs { get; set; }
        public double CharsPerSecond { get; set; }
        public BenchmarkTimings Timings { get; set; } = new();
    }

    private class BenchmarkSummary
    {
        public int TotalFiles { get; set; }
        public int TotalCharacters { get; set; }
        public int TotalWords { get; set; }
        public long TotalElapsedMs { get; set; }
        public double AverageTimePerFileMs { get; set; }
        public double AverageCharsPerSecond { get; set; }
        public long MinTimeMs { get; set; }
        public long MaxTimeMs { get; set; }
        public BenchmarkTimings AggregateTimings { get; set; } = new();
    }

    private class BenchmarkOutput
    {
        public List<BenchmarkFileResult> Files { get; set; } = [];
        public BenchmarkSummary Summary { get; set; } = new();
    }
}
