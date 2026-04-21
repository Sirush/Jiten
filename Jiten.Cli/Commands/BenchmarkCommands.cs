using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Jiten.Core.Data;
using Jiten.Parser.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace Jiten.Cli.Commands;

public class BenchmarkCommands(CliContext context)
{
    private const int SchemaVersion = 2;

    public async Task RunBenchmark(CliOptions options)
    {
        if (string.IsNullOrEmpty(options.Benchmark))
            return;

        if (!Directory.Exists(options.Benchmark))
        {
            Console.WriteLine($"Directory not found: {options.Benchmark}");
            return;
        }

        var txtFiles = Directory.GetFiles(options.Benchmark, "*.txt").OrderBy(f => f).ToList();
        if (txtFiles.Count == 0)
        {
            Console.WriteLine($"No .txt files found in: {options.Benchmark}");
            return;
        }

        if (!Enum.TryParse(options.DeckType ?? "Novel", out MediaType deckType))
            deckType = MediaType.Novel;

        var iterations = Math.Max(1, options.BenchmarkIterations);

        Console.WriteLine($"Files:         {txtFiles.Count}");
        Console.WriteLine($"Media type:    {deckType}");
        Console.WriteLine($"Iterations:    {iterations} per file");
        Console.WriteLine($"Warmup:        {options.BenchmarkWarmup}");
        Console.WriteLine($"Cold mode:     {options.BenchmarkCold} (flush Redis before iter 1 of each file)");
        Console.WriteLine($"Stage timing:  {options.BenchmarkStages}");
        Console.WriteLine();

        // Silence parser's internal per-parse Console.WriteLine timing logs during benchmark.
        ParserBenchmarkInstrumentation.SuppressStageLogs = true;
        ParserBenchmarkInstrumentation.Enabled = options.BenchmarkStages;

        try
        {
            if (options.BenchmarkWarmup)
            {
                Console.WriteLine("Running warmup parse...");
                await Jiten.Parser.Parser.ParseTextToDeck(context.ContextFactory,
                    "これはテストです。日本語のテキストを解析しています。", false, false, deckType);
                Console.WriteLine("Warmup complete.");
                Console.WriteLine();
            }

            var fileResults = new List<BenchmarkFileResult>();

            Console.WriteLine("=== Benchmark Results ===\n");

            for (int f = 0; f < txtFiles.Count; f++)
            {
                var filePath = txtFiles[f];
                var fileName = Path.GetFileName(filePath);
                var content = await File.ReadAllTextAsync(filePath);
                var characterCount = content.Length;

                var iterResults = new List<IterationResult>();

                for (int i = 0; i < iterations; i++)
                {
                    if (options.BenchmarkCold && i == 0)
                        await FlushRedis();

                    // Force GC to a consistent baseline so allocation measurements reflect this iteration only.
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    ParserBenchmarkInstrumentation.Reset();

                    var gen0Before = GC.CollectionCount(0);
                    var gen1Before = GC.CollectionCount(1);
                    var gen2Before = GC.CollectionCount(2);
                    var allocBefore = GC.GetAllocatedBytesForCurrentThread();

                    var sw = Stopwatch.StartNew();
                    var deck = await Jiten.Parser.Parser.ParseTextToDeck(
                        context.ContextFactory, content, false, false, deckType);
                    sw.Stop();

                    var allocAfter = GC.GetAllocatedBytesForCurrentThread();
                    var gen0 = GC.CollectionCount(0) - gen0Before;
                    var gen1 = GC.CollectionCount(1) - gen1Before;
                    var gen2 = GC.CollectionCount(2) - gen2Before;

                    var elapsed = sw.Elapsed.TotalMilliseconds;
                    var stageSnap = options.BenchmarkStages ? ParserBenchmarkInstrumentation.Capture() : null;

                    iterResults.Add(new IterationResult
                    {
                        Iteration = i + 1,
                        IsCold = options.BenchmarkCold && i == 0,
                        ElapsedMs = elapsed,
                        WordCount = deck.DeckWords?.Count ?? 0,
                        UniqueWordCount = deck.UniqueWordCount,
                        SentenceCount = deck.SentenceCount,
                        AllocatedBytes = allocAfter - allocBefore,
                        Gen0 = gen0,
                        Gen1 = gen1,
                        Gen2 = gen2,
                        Stages = stageSnap == null ? null : new StageTimings
                        {
                            SudachiMs = stageSnap.SudachiMs,
                            PreprocessMs = stageSnap.PreprocessMs,
                            DeconjugationLookupMs = stageSnap.DeconjugationLookupMs,
                            ResegmentationMs = stageSnap.ResegmentationMs,
                            AdjacentScoringMs = stageSnap.AdjacentScoringMs,
                            DeconjugatorMs = stageSnap.DeconjugatorMs,
                            DeconjugatorCalls = stageSnap.DeconjugatorCalls,
                            DeconjugatorCacheHits = stageSnap.DeconjugatorCacheHits,
                            DeconjugatorCacheHitRatio = stageSnap.DeconjugatorCacheHitRatio,
                            LookupHits = stageSnap.LookupHits,
                            LookupMisses = stageSnap.LookupMisses,
                            LookupHitRatio = stageSnap.LookupHitRatio,
                            WordCacheHits = stageSnap.WordCacheHits,
                            WordCacheMisses = stageSnap.WordCacheMisses,
                            WordCacheHitRatio = stageSnap.WordCacheHitRatio,
                        }
                    });
                }

                var elapsedSamples = iterResults.Select(r => r.ElapsedMs).ToList();
                var stats = ComputeStats(elapsedSamples);

                var fileResult = new BenchmarkFileResult
                {
                    FileName = fileName,
                    CharacterCount = characterCount,
                    WordCount = iterResults[^1].WordCount,
                    Iterations = iterResults,
                    Stats = stats,
                    CharsPerSecondMedian = stats.Median > 0 ? characterCount / stats.Median * 1000.0 : 0,
                    TokensPerSecondMedian = stats.Median > 0 ? iterResults[^1].WordCount / stats.Median * 1000.0 : 0,
                };

                fileResults.Add(fileResult);

                Console.WriteLine($"{f + 1,3}. {Truncate(fileName, 60),-60}  " +
                                  $"{characterCount,7:N0} chars  {fileResult.WordCount,6:N0} words  " +
                                  $"p50={stats.Median,8:N1}ms  p95={stats.P95,8:N1}ms  " +
                                  $"σ={stats.StdDev,6:N1}  " +
                                  $"{fileResult.CharsPerSecondMedian,8:N0} ch/s");

                if (options.BenchmarkStages)
                {
                    // Show stage breakdown of the warm median iteration for readability.
                    var warmIter = iterResults.Where(r => !r.IsCold).DefaultIfEmpty(iterResults[^1])
                                              .OrderBy(r => r.ElapsedMs).Skip(Math.Max(0, iterResults.Count(r => !r.IsCold) / 2)).First();
                    if (warmIter.Stages is { } s)
                    {
                        Console.WriteLine($"      stages: sudachi={s.SudachiMs:N1}  pre={s.PreprocessMs:N1}  " +
                                          $"deconj+lookup={s.DeconjugationLookupMs:N1}  " +
                                          $"reseg={s.ResegmentationMs:N1}  adj={s.AdjacentScoringMs:N1}");
                        Console.WriteLine($"      deconj: {s.DeconjugatorMs:N1}ms over {s.DeconjugatorCalls:N0} calls " +
                                          $"(cache {s.DeconjugatorCacheHitRatio * 100:N1}%)  " +
                                          $"lookupCache={s.LookupHitRatio * 100:N1}% ({s.LookupHits:N0}/{s.LookupHits + s.LookupMisses:N0})  " +
                                          $"wordCache={s.WordCacheHitRatio * 100:N1}% ({s.WordCacheHits:N0}/{s.WordCacheHits + s.WordCacheMisses:N0})");
                    }
                }
            }

            Console.WriteLine();
            var summary = ComputeSummary(fileResults);
            Console.WriteLine("=== Summary ===");
            Console.WriteLine($"Files:              {summary.TotalFiles}");
            Console.WriteLine($"Total chars:        {summary.TotalCharacters:N0}");
            Console.WriteLine($"Total words:        {summary.TotalWords:N0}");
            Console.WriteLine($"Sum median time:    {summary.SumMedianMs:N0} ms  ({summary.AggregateCharsPerSecond:N0} chars/sec)");
            Console.WriteLine($"Median per-file:    {summary.MedianFileMs:N1} ms");
            Console.WriteLine($"Min / Max (median): {summary.MinMedianMs:N1} / {summary.MaxMedianMs:N1} ms");
            if (options.BenchmarkStages && summary.StageTotals is { } st)
            {
                Console.WriteLine($"Stage totals (ms): sudachi={st.SudachiMs:N0}  pre={st.PreprocessMs:N0}  " +
                                  $"deconj+lookup={st.DeconjugationLookupMs:N0}  reseg={st.ResegmentationMs:N0}  adj={st.AdjacentScoringMs:N0}");
                Console.WriteLine($"Deconjugator:      {st.DeconjugatorMs:N0}ms over {st.DeconjugatorCalls:N0} calls " +
                                  $"(internal cache hit {st.DeconjugatorCacheHitRatio * 100:N1}%)");
                Console.WriteLine($"Redis Lookup:      {st.LookupHitRatio * 100:N1}% hit ({st.LookupHits:N0} hits / {st.LookupMisses:N0} misses)");
                Console.WriteLine($"Redis Word cache:  {st.WordCacheHitRatio * 100:N1}% hit ({st.WordCacheHits:N0} hits / {st.WordCacheMisses:N0} misses)");
            }

            if (!string.IsNullOrEmpty(options.Output))
            {
                var output = new BenchmarkOutput
                {
                    SchemaVersion = SchemaVersion,
                    Environment = CaptureEnvironment(options, deckType),
                    Files = fileResults,
                    Summary = summary
                };

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                await File.WriteAllTextAsync(options.Output, JsonSerializer.Serialize(output, jsonOptions));
                Console.WriteLine();
                Console.WriteLine($"Results written to: {options.Output}");
            }
        }
        finally
        {
            ParserBenchmarkInstrumentation.Enabled = false;
            ParserBenchmarkInstrumentation.SuppressStageLogs = false;
        }
    }

    private Task FlushRedis() => context.FlushRedisAsync();

    private static Stats ComputeStats(List<double> samples)
    {
        if (samples.Count == 0) return new Stats();
        var sorted = samples.OrderBy(x => x).ToList();
        double Percentile(double p)
        {
            if (sorted.Count == 1) return sorted[0];
            var rank = p * (sorted.Count - 1);
            var lo = (int)Math.Floor(rank);
            var hi = (int)Math.Ceiling(rank);
            var frac = rank - lo;
            return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
        }

        var mean = samples.Average();
        var variance = samples.Count > 1
            ? samples.Sum(v => (v - mean) * (v - mean)) / (samples.Count - 1)
            : 0;

        return new Stats
        {
            Count = samples.Count,
            Min = sorted[0],
            Max = sorted[^1],
            Mean = mean,
            Median = Percentile(0.50),
            P95 = Percentile(0.95),
            P99 = Percentile(0.99),
            StdDev = Math.Sqrt(variance),
        };
    }

    private static BenchmarkSummary ComputeSummary(List<BenchmarkFileResult> files)
    {
        var summary = new BenchmarkSummary
        {
            TotalFiles = files.Count,
            TotalCharacters = files.Sum(f => (long)f.CharacterCount),
            TotalWords = files.Sum(f => (long)f.WordCount),
            SumMedianMs = files.Sum(f => f.Stats.Median),
            MedianFileMs = files.Count == 0 ? 0 : files.Select(f => f.Stats.Median).OrderBy(x => x).ElementAt(files.Count / 2),
            MinMedianMs = files.Count == 0 ? 0 : files.Min(f => f.Stats.Median),
            MaxMedianMs = files.Count == 0 ? 0 : files.Max(f => f.Stats.Median),
        };
        summary.AggregateCharsPerSecond = summary.SumMedianMs > 0
            ? summary.TotalCharacters / summary.SumMedianMs * 1000.0 : 0;

        // Aggregate stage totals across all warm iterations.
        var allStages = files.SelectMany(f => f.Iterations)
                             .Where(i => !i.IsCold && i.Stages != null)
                             .Select(i => i.Stages!)
                             .ToList();
        if (allStages.Count > 0)
        {
            var lookupHits = allStages.Sum(s => s.LookupHits);
            var lookupMisses = allStages.Sum(s => s.LookupMisses);
            var wordHits = allStages.Sum(s => s.WordCacheHits);
            var wordMisses = allStages.Sum(s => s.WordCacheMisses);
            var deconjCalls = allStages.Sum(s => s.DeconjugatorCalls);
            var deconjHits = allStages.Sum(s => s.DeconjugatorCacheHits);

            summary.StageTotals = new StageTimings
            {
                SudachiMs = allStages.Sum(s => s.SudachiMs),
                PreprocessMs = allStages.Sum(s => s.PreprocessMs),
                DeconjugationLookupMs = allStages.Sum(s => s.DeconjugationLookupMs),
                ResegmentationMs = allStages.Sum(s => s.ResegmentationMs),
                AdjacentScoringMs = allStages.Sum(s => s.AdjacentScoringMs),
                DeconjugatorMs = allStages.Sum(s => s.DeconjugatorMs),
                DeconjugatorCalls = deconjCalls,
                DeconjugatorCacheHits = deconjHits,
                DeconjugatorCacheHitRatio = deconjCalls == 0 ? 0 : (double)deconjHits / deconjCalls,
                LookupHits = lookupHits,
                LookupMisses = lookupMisses,
                LookupHitRatio = (lookupHits + lookupMisses) == 0 ? 0 : (double)lookupHits / (lookupHits + lookupMisses),
                WordCacheHits = wordHits,
                WordCacheMisses = wordMisses,
                WordCacheHitRatio = (wordHits + wordMisses) == 0 ? 0 : (double)wordHits / (wordHits + wordMisses),
            };
        }
        return summary;
    }

    private static EnvironmentInfo CaptureEnvironment(CliOptions options, MediaType deckType)
    {
        return new EnvironmentInfo
        {
            TimestampUtc = DateTime.UtcNow.ToString("o"),
            MachineName = System.Environment.MachineName,
            OSDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ProcessorCount = System.Environment.ProcessorCount,
            DotNetVersion = System.Environment.Version.ToString(),
            GitCommit = TryGetGitCommit(),
            Label = options.BenchmarkLabel,
            DeckType = deckType.ToString(),
            Iterations = Math.Max(1, options.BenchmarkIterations),
            Warmup = options.BenchmarkWarmup,
            ColdMode = options.BenchmarkCold,
            StageInstrumentation = options.BenchmarkStages,
            BenchmarkDirectory = options.Benchmark,
        };
    }

    private static string? TryGetGitCommit()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return string.IsNullOrEmpty(output) ? null : output;
        }
        catch { return null; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    private class IterationResult
    {
        public int Iteration { get; set; }
        public bool IsCold { get; set; }
        public double ElapsedMs { get; set; }
        public int WordCount { get; set; }
        public int UniqueWordCount { get; set; }
        public int SentenceCount { get; set; }
        public long AllocatedBytes { get; set; }
        public int Gen0 { get; set; }
        public int Gen1 { get; set; }
        public int Gen2 { get; set; }
        public StageTimings? Stages { get; set; }
    }

    private class StageTimings
    {
        public double SudachiMs { get; set; }
        public double PreprocessMs { get; set; }
        public double DeconjugationLookupMs { get; set; }
        public double ResegmentationMs { get; set; }
        public double AdjacentScoringMs { get; set; }

        public double DeconjugatorMs { get; set; }
        public long DeconjugatorCalls { get; set; }
        public long DeconjugatorCacheHits { get; set; }
        public double DeconjugatorCacheHitRatio { get; set; }

        public long LookupHits { get; set; }
        public long LookupMisses { get; set; }
        public double LookupHitRatio { get; set; }

        public long WordCacheHits { get; set; }
        public long WordCacheMisses { get; set; }
        public double WordCacheHitRatio { get; set; }
    }

    private class Stats
    {
        public int Count { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public double Mean { get; set; }
        public double Median { get; set; }
        public double P95 { get; set; }
        public double P99 { get; set; }
        public double StdDev { get; set; }
    }

    private class BenchmarkFileResult
    {
        public string FileName { get; set; } = "";
        public int CharacterCount { get; set; }
        public int WordCount { get; set; }
        public List<IterationResult> Iterations { get; set; } = [];
        public Stats Stats { get; set; } = new();
        public double CharsPerSecondMedian { get; set; }
        public double TokensPerSecondMedian { get; set; }
    }

    private class BenchmarkSummary
    {
        public int TotalFiles { get; set; }
        public long TotalCharacters { get; set; }
        public long TotalWords { get; set; }
        public double SumMedianMs { get; set; }
        public double MedianFileMs { get; set; }
        public double MinMedianMs { get; set; }
        public double MaxMedianMs { get; set; }
        public double AggregateCharsPerSecond { get; set; }
        public StageTimings? StageTotals { get; set; }
    }

    private class EnvironmentInfo
    {
        public string TimestampUtc { get; set; } = "";
        public string MachineName { get; set; } = "";
        public string OSDescription { get; set; } = "";
        public int ProcessorCount { get; set; }
        public string DotNetVersion { get; set; } = "";
        public string? GitCommit { get; set; }
        public string? Label { get; set; }
        public string DeckType { get; set; } = "";
        public int Iterations { get; set; }
        public bool Warmup { get; set; }
        public bool ColdMode { get; set; }
        public bool StageInstrumentation { get; set; }
        public string? BenchmarkDirectory { get; set; }
    }

    private class BenchmarkOutput
    {
        public int SchemaVersion { get; set; }
        public EnvironmentInfo Environment { get; set; } = new();
        public List<BenchmarkFileResult> Files { get; set; } = [];
        public BenchmarkSummary Summary { get; set; } = new();
    }
}
