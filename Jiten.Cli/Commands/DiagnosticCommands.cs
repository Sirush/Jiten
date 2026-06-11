using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Parser;
using Jiten.Parser.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;
using WanaKanaShaapu;

namespace Jiten.Cli.Commands;

public class DiagnosticCommands(CliContext context)
{
    public async Task ParseTest(CliOptions options)
    {
        var text = options.ParseTest!;

        if (text.StartsWith("@"))
        {
            var filePath = text[1..];
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"File not found: {filePath}");
                return;
            }
            text = await File.ReadAllTextAsync(filePath);
        }

        var currentDir = Directory.GetCurrentDirectory();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(currentDir)
            .AddJsonFile(Path.Combine(Environment.CurrentDirectory, "..", "Shared", "sharedsettings.json"), optional: true)
            .AddJsonFile("sharedsettings.json", optional: true)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();
        var dictionaryPath = configuration.GetValue<string>("DictionaryPath");
        var dictionaryExists = !string.IsNullOrEmpty(dictionaryPath) && File.Exists(dictionaryPath);

        var diagnostics = new ParserDiagnostics { InputText = text };
        var sw = Stopwatch.StartNew();

        List<DeckDictionaryEntry>? dictEntries = null;
        var names = options.ParseTestNames?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (names is { Count: > 0 })
        {
            dictEntries = names.Select(n => new DeckDictionaryEntry { Surface = n, EntryType = DeckDictionaryEntryType.Name }).ToList();
            Console.WriteLine($"Injecting {dictEntries.Count} user-dict name(s): {string.Join(", ", names)}");
        }

        var deckWords = dictEntries != null
            ? (await Jiten.Parser.Parser.ParseTextToDeck(context.ContextFactory, text, diagnostics: diagnostics, dictionaryEntries: dictEntries)).DeckWords?.Select(dw => dw).Cast<DeckWord>().ToList() ?? []
            : await Jiten.Parser.Parser.ParseText(context.ContextFactory, text, diagnostics: diagnostics);

        sw.Stop();
        diagnostics.TotalElapsedMs = sw.ElapsedMilliseconds;

        var output = new
        {
            InputText = text,
            TotalElapsedMs = sw.ElapsedMilliseconds,
            TokenCount = deckWords.Count,
            Sudachi = diagnostics.Sudachi,
            TokenStages = diagnostics.TokenStages,
            RunSummary = diagnostics.RunSummary,
            Tokens = deckWords.Select(w => new
            {
                w.OriginalText,
                w.WordId,
                w.ReadingIndex,
                PartsOfSpeech = w.PartsOfSpeech?.Select(p => p.ToString()).ToList(),
                w.Conjugations
            }).ToList(),
            FormScoring = diagnostics.Results,
            AdjacentScoring = diagnostics.AdjacentScoring,
            DroppedTokens = diagnostics.DroppedTokens,
            ParserEvents = diagnostics.ParserEvents,
            TransitionViolations = diagnostics.TransitionViolations
        };

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };
        var json = JsonSerializer.Serialize(output, jsonOptions);

        if (!string.IsNullOrEmpty(options.ParseTestOutput))
        {
            await File.WriteAllTextAsync(options.ParseTestOutput, json);
            Console.WriteLine($"Diagnostics written to {options.ParseTestOutput}");
        }
        else
        {
            Console.WriteLine(json);
        }
    }

    public async Task ParseDeckTest(CliOptions options)
    {
        var text = options.ParseDeckTest!;
        if (text.StartsWith("@"))
            text = text[1..];

        if (File.Exists(text))
            text = await File.ReadAllTextAsync(text);

        var watchWord = options.WatchWord;

        Console.WriteLine($"Parsing deck ({text.Length:N0} chars)...");
        var sw = Stopwatch.StartNew();
        var deck = await Jiten.Parser.Parser.ParseTextToDeck(context.ContextFactory, text);
        sw.Stop();
        Console.WriteLine($"Deck parse done in {sw.ElapsedMilliseconds}ms. {deck.DeckWords?.Count ?? 0} unique words.");

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

        if (watchWord == null)
        {
            var output = new
            {
                TotalElapsedMs = sw.ElapsedMilliseconds,
                Tokens = deck.DeckWords?.Select(w => new { w.OriginalText, w.WordId, w.ReadingIndex, w.Occurrences }).ToList()
            };
            var json = JsonSerializer.Serialize(output, jsonOptions);
            if (!string.IsNullOrEmpty(options.ParseTestOutput))
            {
                await File.WriteAllTextAsync(options.ParseTestOutput, json);
                Console.WriteLine($"Written to {options.ParseTestOutput}");
            }
            else Console.WriteLine(json);
            return;
        }

        // --- Focused watch-word output ---
        var deckResult = deck.DeckWords?.FirstOrDefault(w => w.OriginalText == watchWord);

        // Find example sentences where the watch word appears
        var watchExampleSentences = deck.ExampleSentences?
            .Where(s => s.Text.Contains(watchWord))
            .Select(s => new
            {
                s.Text,
                Words = s.Words?.Select(w => new { w.WordId, w.ReadingIndex, w.Position, w.Length }).ToList()
            })
            .ToList();

        // Find sentences in raw text that contain the watch word, and re-parse each with full diagnostics
        var sentencesContainingWord = text
            .Split(['\n', '。', '！', '？', '…'], StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s.Contains(watchWord))
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct()
            .Take(5)
            .ToList();

        var standaloneResults = new List<object>();
        foreach (var sentence in sentencesContainingWord)
        {
            var diag = new ParserDiagnostics { InputText = sentence };
            var tokens = await Jiten.Parser.Parser.ParseText(context.ContextFactory, sentence, diagnostics: diag);
            var watchToken = tokens.FirstOrDefault(t => t.OriginalText == watchWord);
            var watchScoring = diag.Results.FirstOrDefault(r => r.Text == watchWord);
            standaloneResults.Add(new
            {
                Sentence = sentence,
                StandaloneResult = watchToken == null ? null : new { watchToken.OriginalText, watchToken.WordId, watchToken.ReadingIndex },
                FormScoring = watchScoring
            });
        }

        var focusedOutput = new
        {
            WatchWord = watchWord,
            DeckResult = deckResult == null ? null : new
            {
                deckResult.OriginalText, deckResult.WordId, deckResult.ReadingIndex, deckResult.Occurrences,
                PartsOfSpeech = deckResult.PartsOfSpeech?.Select(p => p.ToString()).ToList()
            },
            ExampleSentencesContainingWord = watchExampleSentences,
            StandaloneParsePerSentence = standaloneResults
        };

        var focusedJson = JsonSerializer.Serialize(focusedOutput, jsonOptions);
        if (!string.IsNullOrEmpty(options.ParseTestOutput))
        {
            await File.WriteAllTextAsync(options.ParseTestOutput, focusedJson);
            Console.WriteLine($"Written to {options.ParseTestOutput}");
        }
        else Console.WriteLine(focusedJson);
    }

    public Task DeconjugateTest(CliOptions options)
    {
        var text = options.DeconjugateTest!;
        var sw = Stopwatch.StartNew();
        var forms = Deconjugator.Instance.Deconjugate(text);
        sw.Stop();

        var output = new
        {
            InputText = text,
            ElapsedMs = sw.ElapsedMilliseconds,
            FormCount = forms.Count,
            Forms = forms.OrderBy(f => f.Text).Select(f => new
            {
                f.Text,
                f.Tags,
                f.Process,
                SeenText = f.SeenText.OrderBy(s => s).ToList()
            }).ToList()
        };

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };
        var json = JsonSerializer.Serialize(output, jsonOptions);

        if (!string.IsNullOrEmpty(options.ParseTestOutput))
        {
            File.WriteAllText(options.ParseTestOutput, json);
            Console.WriteLine($"Diagnostics written to {options.ParseTestOutput}");
        }
        else
        {
            Console.WriteLine(json);
        }

        return Task.CompletedTask;
    }

    public async Task MineMargins(CliOptions options)
    {
        var input = options.MineMargins!;
        if (input.StartsWith("@"))
            input = input[1..];

        if (File.Exists(input))
        {
            await MineMarginsFromFile(input, options);
            return;
        }

        Console.WriteLine($"Mining margins (band=[{options.MarginMin},{options.MarginThreshold}), limit={options.MarginLimit}, {input.Length:N0} chars)...");
        var sw = Stopwatch.StartNew();
        var findings = MarginMiner.MineText(input, options.MarginThreshold, options.MarginLimit, options.MarginMin,
                                            (done, total) => Console.WriteLine($"  {done}/{total} sentences..."));
        sw.Stop();

        Console.WriteLine($"Found {findings.Count} distinct ambiguity types in {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine();
        PrintTopFindings(findings);

        if (!string.IsNullOrEmpty(options.ParseTestOutput))
        {
            await File.WriteAllTextAsync(options.ParseTestOutput, JsonSerializer.Serialize(findings, MarginJsonOptions));
            Console.WriteLine($"Findings written to {options.ParseTestOutput}");
        }
    }

    private static readonly JsonSerializerOptions MarginJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public async Task AuditUserDic(CliOptions options)
    {
        var input = options.AuditUserDic!;
        if (input.StartsWith("@"))
            input = input[1..];

        var xmlPath = options.UserDicXmlPath
                      ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "user_dic.xml");
        if (!File.Exists(xmlPath))
        {
            Console.WriteLine($"user_dic.xml not found: {xmlPath} (use --user-dic-xml)");
            return;
        }

        var session = new UserDicAuditor.Session(xmlPath);
        var sw = Stopwatch.StartNew();

        if (File.Exists(input))
        {
            const int ReportIntervalSeconds = 15;
            const int SaveIntervalSeconds = 60;

            var totalBytes = new FileInfo(input).Length;
            var outputPath = !string.IsNullOrEmpty(options.ParseTestOutput)
                ? options.ParseTestOutput
                : input + ".userdic-audit.json";
            Console.WriteLine($"Auditing user dictionary against {input} ({totalBytes / 1048576.0:N0} MB), " +
                              $"snapshots to {outputPath} every {SaveIntervalSeconds}s");

            var lastReport = TimeSpan.Zero;
            var lastSave = TimeSpan.Zero;

            await using var stream = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                    1 << 20, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20);

            foreach (var sentence in MarginMiner.EnumerateSentences(reader))
            {
                session.Process(sentence);

                if (session.SentencesSeen % 512 != 0)
                    continue;

                var elapsed = sw.Elapsed;
                if ((elapsed - lastReport).TotalSeconds >= ReportIntervalSeconds)
                {
                    lastReport = elapsed;
                    var mbPerSec = stream.Position / 1048576.0 / Math.Max(1, elapsed.TotalSeconds);
                    Console.WriteLine($"[{100.0 * stream.Position / totalBytes,5:F1}%] {mbPerSec:F2} MB/s | " +
                                      $"{session.SentencesSeen:N0} sentences | {session.Captures:N0} captures | " +
                                      $"{session.DistinctEntries:N0} entries flagged");
                }
                if ((elapsed - lastSave).TotalSeconds >= SaveIntervalSeconds)
                {
                    lastSave = elapsed;
                    await SaveAuditSnapshot(outputPath, session, options.MarginLimit, complete: false);
                }
            }

            await SaveAuditSnapshot(outputPath, session, options.MarginLimit, complete: true);
            Console.WriteLine($"Done in {sw.Elapsed:hh\\:mm\\:ss}, results in {outputPath}");
        }
        else
        {
            foreach (var sentence in MarginMiner.EnumerateSentences(new StringReader(input)))
                session.Process(sentence);

            if (!string.IsNullOrEmpty(options.ParseTestOutput))
                await SaveAuditSnapshot(options.ParseTestOutput, session, options.MarginLimit, complete: true);
        }

        Console.WriteLine($"{session.SentencesTokenized:N0} sentences, {session.Captures:N0} boundary-crossing captures, " +
                          $"{session.DistinctEntries:N0} distinct entries");
        foreach (var f in session.Snapshot(Math.Min(options.MarginLimit, 50)))
            Console.WriteLine($"[x{f.Occurrences}] {f.Entry}  crosses: {f.CrossedTokens}   " +
                              $"with: {Truncate(f.WithDic, 30)}  without: {Truncate(f.WithoutDic, 30)}");
    }

    private static async Task SaveAuditSnapshot(string outputPath, UserDicAuditor.Session session, int limit, bool complete)
    {
        var snapshot = new
        {
            Status = new
            {
                Complete = complete,
                session.SentencesSeen,
                session.SentencesTokenized,
                session.DuplicatesSkipped,
                session.TokenizeErrors,
                session.Captures,
                session.SpacedSentencesCollapsed,
                session.DistinctEntries
            },
            Findings = session.Snapshot(limit)
        };
        var tempPath = outputPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(snapshot, MarginJsonOptions));
        File.Move(tempPath, outputPath, overwrite: true);
    }

    private async Task MineMarginsFromFile(string path, CliOptions options)
    {
        const int ReportIntervalSeconds = 15;
        const int SaveIntervalSeconds = 60;

        var totalBytes = new FileInfo(path).Length;
        var outputPath = !string.IsNullOrEmpty(options.ParseTestOutput) ? options.ParseTestOutput : path + ".margins.json";

        Console.WriteLine($"Mining margins from {path} ({totalBytes / 1048576.0:N0} MB, band=[{options.MarginMin},{options.MarginThreshold}), limit={options.MarginLimit})");
        Console.WriteLine($"Streaming pass; snapshot saved to {outputPath} every {SaveIntervalSeconds}s");

        var session = new MarginMiner.Session(options.MarginThreshold, options.MarginMin);
        var sw = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var lastSave = TimeSpan.Zero;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                1 << 20, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20);

        foreach (var sentence in MarginMiner.EnumerateSentences(reader))
        {
            session.Process(sentence);

            if (session.SentencesSeen % 512 != 0)
                continue;

            var elapsed = sw.Elapsed;
            if ((elapsed - lastReport).TotalSeconds >= ReportIntervalSeconds)
            {
                lastReport = elapsed;
                ReportMiningProgress(session, stream.Position, totalBytes, elapsed);
            }
            if ((elapsed - lastSave).TotalSeconds >= SaveIntervalSeconds)
            {
                lastSave = elapsed;
                await SaveMiningSnapshot(outputPath, session, options.MarginLimit, stream.Position, totalBytes, complete: false);
            }
        }

        sw.Stop();
        ReportMiningProgress(session, totalBytes, totalBytes, sw.Elapsed);
        await SaveMiningSnapshot(outputPath, session, options.MarginLimit, totalBytes, totalBytes, complete: true);

        var findings = session.Snapshot(options.MarginLimit);
        Console.WriteLine();
        Console.WriteLine($"Done in {sw.Elapsed:hh\\:mm\\:ss}: {findings.Count} ambiguity types reported " +
                          $"({session.DistinctTypes:N0} retained, {session.TypesPruned:N0} rare ones pruned), final results in {outputPath}");
        Console.WriteLine();
        PrintTopFindings(findings);
    }

    private static void ReportMiningProgress(MarginMiner.Session session, long bytesRead, long totalBytes, TimeSpan elapsed)
    {
        var mbPerSec = bytesRead / 1048576.0 / Math.Max(1, elapsed.TotalSeconds);
        var eta = bytesRead > 0 ? TimeSpan.FromSeconds((totalBytes - bytesRead) / 1048576.0 / Math.Max(0.001, mbPerSec)) : TimeSpan.Zero;
        Console.WriteLine($"[{100.0 * bytesRead / totalBytes,5:F1}%] {bytesRead / 1048576.0:N0}/{totalBytes / 1048576.0:N0} MB | " +
                          $"{mbPerSec:F2} MB/s | ETA {eta:hh\\:mm\\:ss} | " +
                          $"{session.SentencesSeen:N0} sentences ({session.DuplicatesSkipped:N0} dupes, {session.TokenizeErrors:N0} errors) | " +
                          $"{session.DistinctTypes:N0} types");
    }

    private async Task SaveMiningSnapshot(string outputPath, MarginMiner.Session session, int limit,
                                          long bytesRead, long totalBytes, bool complete)
    {
        var snapshot = new
        {
            Status = new
            {
                Complete = complete,
                PercentComplete = Math.Round(100.0 * bytesRead / totalBytes, 1),
                session.SentencesSeen,
                session.SentencesTokenized,
                session.DuplicatesSkipped,
                session.TokenizeErrors,
                session.DistinctTypes,
                session.TypesPruned,
                session.DedupResets,
                session.SpacedSentencesCollapsed
            },
            Findings = session.Snapshot(limit)
        };

        // write-then-rename so a kill mid-save never corrupts the snapshot
        var tempPath = outputPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(snapshot, MarginJsonOptions));
        File.Move(tempPath, outputPath, overwrite: true);
    }

    private static void PrintTopFindings(List<MarginMiner.MarginFinding> findings)
    {
        foreach (var f in findings.Take(50))
        {
            Console.WriteLine($"[x{f.Occurrences}, margin {f.MinMargin}] {f.AmbiguousSpan}   e.g. {Truncate(f.Segmentation, 70)}");
        }
        if (findings.Count > 50)
            Console.WriteLine($"... ({findings.Count - 50} more in the JSON output)");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public async Task RunParserTests(CliOptions options)
    {
        var runner = new DiagnosticTestRunner(context.ContextFactory);
        var result = await runner.RunSegmentationTests();

        Console.WriteLine($"=== Parser Test Results ===");
        Console.WriteLine($"Total: {result.TotalTests}, Passed: {result.Passed}, Failed: {result.Failed}");
        Console.WriteLine();

        if (result.Failures.Count == 0)
        {
            Console.WriteLine("All tests passed!");
            return;
        }

        Console.WriteLine($"=== Failures ({result.Failures.Count}) ===");
        Console.WriteLine();

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        foreach (var failure in result.Failures.Take(20))
        {
            Console.WriteLine($"Input: {failure.Input}");
            Console.WriteLine($"Expected: [{string.Join(", ", failure.Expected)}]");
            Console.WriteLine($"Actual:   [{string.Join(", ", failure.Actual)}]");

            if (failure.Analysis != null)
            {
                Console.WriteLine($"Type: {failure.Analysis.Type}");
                Console.WriteLine($"Cause: {failure.Analysis.ProbableCause ?? "Unknown"}");
                if (!string.IsNullOrEmpty(failure.Analysis.SuggestedFix))
                {
                    Console.WriteLine($"Suggested Fix:");
                    Console.WriteLine(failure.Analysis.SuggestedFix);
                }
            }

            Console.WriteLine();
        }

        if (result.Failures.Count > 20)
        {
            Console.WriteLine($"... and {result.Failures.Count - 20} more failures.");
        }

        if (!string.IsNullOrEmpty(options.ParseTestOutput))
        {
            var json = JsonSerializer.Serialize(result, jsonOptions);
            await File.WriteAllTextAsync(options.ParseTestOutput, json);
            Console.WriteLine($"Full diagnostics written to {options.ParseTestOutput}");
        }
    }

    public async Task RunFormTests(CliOptions options)
    {
        var runner = new DiagnosticTestRunner(context.ContextFactory);
        var result = await runner.RunFormSelectionTests();

        Console.WriteLine($"=== Form Selection Test Results ===");
        Console.WriteLine($"Total: {result.TotalTests}, Passed: {result.Passed}, Failed: {result.Failed}");
        Console.WriteLine();

        if (result.Failures.Count == 0)
        {
            Console.WriteLine("All tests passed!");
            return;
        }

        Console.WriteLine($"=== Failures ({result.Failures.Count}) ===");
        Console.WriteLine();

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        foreach (var failure in result.Failures.Take(20))
        {
            Console.WriteLine($"Input: {failure.Input}");
            Console.WriteLine($"Expected: token='{failure.ExpectedToken}' wordId={failure.ExpectedWordId} readingIndex={failure.ExpectedReadingIndex}");
            Console.WriteLine($"Actual:   {(failure.ActualWordId.HasValue ? $"wordId={failure.ActualWordId} readingIndex={failure.ActualReadingIndex}" : "token not found in results")}");
            Console.WriteLine($"Reason: {failure.Reason}");
            Console.WriteLine();
        }

        if (result.Failures.Count > 20)
        {
            Console.WriteLine($"... and {result.Failures.Count - 20} more failures.");
        }

        if (!string.IsNullOrEmpty(options.ParseTestOutput))
        {
            var json = JsonSerializer.Serialize(result, jsonOptions);
            await File.WriteAllTextAsync(options.ParseTestOutput, json);
            Console.WriteLine($"Full diagnostics written to {options.ParseTestOutput}");
        }
    }

    public async Task SearchWord(string query)
    {
        await using var context1 = await context.ContextFactory.CreateDbContextAsync();

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        if (int.TryParse(query, out int wordId))
        {
            var word = await context1.JMDictWords
                .Include(w => w.Definitions)
                .Include(w => w.Forms)
                .FirstOrDefaultAsync(w => w.WordId == wordId);

            if (word == null)
            {
                Console.WriteLine($"No word found with WordId: {wordId}");
                return;
            }

            PrintWord(word);
            return;
        }

        var words = await context1.JMDictWords
            .Include(w => w.Definitions)
            .Include(w => w.Forms)
            .Where(w => w.Forms.Any(f => f.Text == query))
            .Take(20)
            .ToListAsync();

        if (words.Count == 0)
        {
            Console.WriteLine($"No words found with reading: {query}");
            return;
        }

        Console.WriteLine($"Found {words.Count} word(s) matching '{query}':");
        Console.WriteLine();

        foreach (var word in words)
        {
            PrintWord(word);
            Console.WriteLine("---");
        }
    }

    public async Task SearchLookup(string query)
    {
        await using var context1 = await context.ContextFactory.CreateDbContextAsync();

        var hiraganaQuery = WanaKana.ToHiragana(query);

        var lookups = await context1.Lookups
            .Where(l => l.LookupKey == query || l.LookupKey == hiraganaQuery)
            .ToListAsync();

        if (lookups.Count == 0)
        {
            Console.WriteLine($"No lookups found for: {query}");
            if (hiraganaQuery != query)
                Console.WriteLine($"Also tried hiragana: {hiraganaQuery}");
            return;
        }

        Console.WriteLine($"Found {lookups.Count} lookup(s) for '{query}':");
        Console.WriteLine();

        var wordIds = lookups.Select(l => l.WordId).Distinct().ToList();
        Console.WriteLine($"WordIds: [{string.Join(", ", wordIds)}]");
        Console.WriteLine();

        var words = await context1.JMDictWords
            .Include(w => w.Definitions)
            .Where(w => wordIds.Contains(w.WordId))
            .ToListAsync();

        foreach (var word in words)
        {
            PrintWord(word);
            Console.WriteLine("---");
        }
    }

    public async Task ScanConfidence(CliOptions options)
    {
        if (string.IsNullOrEmpty(options.Input))
        {
            Console.WriteLine("--scan-confidence requires --input <corpus.txt>");
            return;
        }

        if (!File.Exists(options.Input))
        {
            Console.WriteLine($"File not found: {options.Input}");
            return;
        }

        var threshold = options.Threshold;
        var lines = await File.ReadAllLinesAsync(options.Input);
        var events = new List<object>();
        int totalTokens = 0, lowConfidenceCount = 0;

        Console.WriteLine($"Scanning {lines.Length} lines with threshold={threshold}...");

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var diagnostics = new ParserDiagnostics { InputText = line };
            await Jiten.Parser.Parser.ParseText(context.ContextFactory, line, diagnostics: diagnostics);

            totalTokens += diagnostics.Results.Count;
            var lowConf = diagnostics.GetLowConfidenceResults(threshold).ToList();
            lowConfidenceCount += lowConf.Count;

            foreach (var result in lowConf)
            {
                var best = result.Candidates.FirstOrDefault(c => c.IsSelected);
                var second = result.Candidates.Where(c => !c.IsSelected).OrderByDescending(c => c.TotalScore).FirstOrDefault();

                events.Add(new
                {
                    surface = result.Text,
                    sentence = line,
                    bestWordId = result.WordId,
                    bestScore = best?.TotalScore,
                    secondWordId = second?.WordId,
                    secondScore = second?.TotalScore,
                    margin = result.MarginToSecond,
                    confidenceLevel = result.ConfidenceLevel
                });
            }
        }

        Console.WriteLine($"Scanned {totalTokens} tokens across {lines.Length} lines, found {lowConfidenceCount} low-confidence (threshold={threshold}).");

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };
        var json = JsonSerializer.Serialize(events, jsonOptions);

        if (!string.IsNullOrEmpty(options.ParseTestOutput))
        {
            await File.WriteAllTextAsync(options.ParseTestOutput, json);
            Console.WriteLine($"Results written to {options.ParseTestOutput}");
        }
        else
        {
            Console.WriteLine(json);
        }
    }

    /// Every merged/conjugated token must carry a deconjugation chain.
    /// Flags tokens whose OriginalText matches none of the resolved word's forms (i.e. the
    /// surface is inflected or beam-merged) while Conjugations is empty — each hit is a
    /// subsidiary-verb merge with no deconjugator.json counterpart (e.g. ~て下さる before fix).
    public async Task AuditConjugations(CliOptions options)
    {
        if (string.IsNullOrEmpty(options.Input))
        {
            Console.WriteLine("--audit-conjugations requires --input <corpus.txt>");
            return;
        }

        if (!File.Exists(options.Input))
        {
            Console.WriteLine($"File not found: {options.Input}");
            return;
        }

        var lines = await File.ReadAllLinesAsync(options.Input);
        Console.WriteLine($"Auditing {lines.Length} lines...");

        // (wordId, originalText) → (count, example sentence)
        var suspects = new Dictionary<(int WordId, string Surface), (int Count, string Example)>();
        int totalTokens = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var words = await Jiten.Parser.Parser.ParseText(context.ContextFactory, line);
            foreach (var word in words)
            {
                totalTokens++;
                if (word.Conjugations is { Count: > 0 }) continue;
                if (string.IsNullOrEmpty(word.OriginalText) || word.OriginalText.Length < 2) continue;

                var key = (word.WordId, word.OriginalText);
                suspects[key] = suspects.TryGetValue(key, out var prev)
                    ? (prev.Count + word.Occurrences, prev.Example)
                    : (word.Occurrences, line);
            }
        }

        // Resolve forms in one batch and keep only tokens whose surface matches no form
        // (raw or kana-fold) — those are merged/inflected surfaces missing their chain.
        var wordIds = suspects.Keys.Select(k => k.WordId).Distinct().ToList();
        var formsByWord = new Dictionary<int, List<string>>();
        await using (var ctx = await context.ContextFactory.CreateDbContextAsync())
        {
            var rows = await ctx.JMDictWords.AsNoTracking()
                                .Where(w => wordIds.Contains(w.WordId))
                                .Select(w => new { w.WordId, Forms = w.Forms.Select(f => f.Text).ToList() })
                                .ToListAsync();
            foreach (var row in rows)
                formsByWord[row.WordId] = row.Forms;
        }

        static string Fold(string s) => KanaNormalizer.Normalize(WanaKana.ToHiragana(s));

        var findings = suspects
                       .Where(kv =>
                       {
                           if (!formsByWord.TryGetValue(kv.Key.WordId, out var forms)) return true;
                           var surface = kv.Key.Surface;
                           var surfaceFold = Fold(surface);
                           bool matches = forms.Any(f => f == surface || Fold(f) == surfaceFold);
                           // Form + な/に attachments (na-adjectives/adverbs) are not missing chains
                           if (!matches && surface.Length > 1 && (surface[^1] == 'な' || surface[^1] == 'に'))
                           {
                               var stripped = surface[..^1];
                               var strippedFold = Fold(stripped);
                               matches = forms.Any(f => f == stripped || Fold(f) == strippedFold);
                           }
                           return !matches;
                       })
                       .OrderByDescending(kv => kv.Value.Count)
                       .Select(kv => new
                       {
                           surface = kv.Key.Surface,
                           wordId = kv.Key.WordId,
                           occurrences = kv.Value.Count,
                           example = kv.Value.Example,
                           forms = formsByWord.GetValueOrDefault(kv.Key.WordId)
                       })
                       .ToList();

        Console.WriteLine($"Scanned {totalTokens} tokens; {findings.Count} merged-surface tokens with empty Conjugations.");

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };
        var json = JsonSerializer.Serialize(findings, jsonOptions);

        if (!string.IsNullOrEmpty(options.ParseTestOutput))
        {
            await File.WriteAllTextAsync(options.ParseTestOutput, json);
            Console.WriteLine($"Results written to {options.ParseTestOutput}");
        }
        else
        {
            Console.WriteLine(json);
        }
    }

    public async Task FlushRedisCache()
    {
        var redisConnectionString = context.Configuration.GetConnectionString("Redis");
        if (string.IsNullOrEmpty(redisConnectionString))
        {
            Console.WriteLine("Redis connection string not found in configuration.");
            return;
        }

        try
        {
            var connection = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
            var redisDb = connection.GetDatabase();
            redisDb.Execute("FLUSHALL");
            await connection.CloseAsync();

            Console.WriteLine("Redis cache flushed successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to flush Redis cache: {ex.Message}");
        }
    }

    private static void PrintWord(JmDictWord word)
    {
        Console.WriteLine($"WordId: {word.WordId}");
        var orderedForms = word.Forms.OrderBy(f => f.ReadingIndex).ToList();
        Console.WriteLine($"Readings: [{string.Join(", ", orderedForms.Select(f => f.Text))}]");
        Console.WriteLine($"FormTypes: [{string.Join(", ", orderedForms.Select(f => f.FormType))}]");
        Console.WriteLine($"PartsOfSpeech: [{string.Join(", ", word.PartsOfSpeech)}]");
        Console.WriteLine($"PitchAccents: [{string.Join(", ", word.PitchAccents ?? [])}]");
        Console.WriteLine($"Priorities: [{string.Join(", ", word.Priorities ?? [])}]");
        Console.WriteLine($"Origin: {word.Origin}");

        if (word.Definitions?.Count > 0)
        {
            Console.WriteLine("Definitions:");
            foreach (var def in word.Definitions.Take(3))
            {
                var posStr = def.PartsOfSpeech.Count > 0 ? string.Join(", ", def.PartsOfSpeech) : "";
                var meanings = def.EnglishMeanings.Count > 0 ? string.Join("; ", def.EnglishMeanings) : "(no English)";
                Console.WriteLine($"  [{posStr}] {meanings}");
            }
        }
    }
}
