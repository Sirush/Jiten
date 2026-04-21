using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Jiten.Core.Data.JMDict;
using Jiten.Parser;
using Jiten.Parser.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

        var deckWords = await Jiten.Parser.Parser.ParseText(context.ContextFactory, text, diagnostics: diagnostics);

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
            TransitionViolations = diagnostics.TransitionViolations,
            ResegmentationPaths = diagnostics.ResegmentationPaths,
            BeamSentences = diagnostics.BeamSentences
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

    public void SearchConj(string surface)
    {
        var binPath = Jiten.Parser.Resolution.ConjugationTableBinaryFile.DefaultPath;
        var table = Jiten.Parser.Resolution.ConjugationTableBinaryFile.TryRead(binPath, Console.WriteLine);
        if (table == null)
        {
            Console.WriteLine($"Failed to load conjugation binary at {binPath}");
            return;
        }

        var hits = table.GetHits(surface);
        Console.WriteLine($"Surface '{surface}' → {hits.Length} hit(s)");
        foreach (var h in hits)
        {
            var chain = h.Chain == null ? "(null)" : string.Join("|", h.Chain);
            Console.WriteLine($"  wordId={h.WordId} formIndex={h.FormIndex} chain=[{chain}]");
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

    public Task FlushRedisCache() => context.FlushRedisAsync();

    public async Task CountAmbiguous()
    {
        await using var ctx = await context.ContextFactory.CreateDbContextAsync();

        Console.WriteLine("Loading lookups...");
        var rawLookups = await ctx.Lookups
            .Select(l => new { l.LookupKey, l.WordId })
            .ToListAsync();

        var lookups = rawLookups
            .GroupBy(l => l.LookupKey)
            .Select(g => new { Surface = g.Key, WordIds = g.Select(l => l.WordId).Distinct().ToList() })
            .Where(g => g.WordIds.Count > 1)
            .ToList();

        Console.WriteLine($"Surfaces with 2+ distinct WordIds: {lookups.Count}");

        var allWordIds = lookups.SelectMany(l => l.WordIds).Distinct().ToList();
        Console.WriteLine($"Loading {allWordIds.Count} words with forms...");

        var words = await ctx.JMDictWords
            .Include(w => w.Forms)
            .Where(w => allWordIds.Contains(w.WordId))
            .ToDictionaryAsync(w => w.WordId);

        Console.WriteLine($"Loaded {words.Count} words. Analyzing...");

        int ambiguousSurfaces = 0;
        int totalMultiEntry = lookups.Count;
        var examples = new List<(string Surface, int ReadingCount, List<(int WordId, string Reading, string Pos)> Entries)>();

        foreach (var lookup in lookups)
        {
            var entries = new List<(int WordId, HashSet<string> Readings, List<string> Pos, bool HasPriority)>();

            foreach (var wid in lookup.WordIds)
            {
                if (wid >= 5000000) continue;
                if (!words.TryGetValue(wid, out var word)) continue;

                var matchingForm = word.Forms.FirstOrDefault(f => f.Text == lookup.Surface);
                if (matchingForm == null) continue;

                var readings = new HashSet<string>();
                foreach (var f in word.Forms)
                    if (f.FormType == JmDictFormType.KanaForm)
                        readings.Add(f.Text);
                if (readings.Count == 0) readings.Add(lookup.Surface);

                bool hasPri = (word.Priorities != null && word.Priorities.Count > 0)
                    || word.Forms.Any(f => f.Priorities != null && f.Priorities.Count > 0);

                entries.Add((wid, readings, word.PartsOfSpeech, hasPri));
            }

            if (entries.Count < 2) continue;

            // Only consider entries that are common enough to appear in real text.
            // Entries with Priorities (ichi1, news1, nf##, etc.) are in frequency lists.
            var common = entries.Where(e => e.HasPriority).ToList();
            if (common.Count < 2) continue;

            bool hasOverlappingPos = false;
            for (int i = 0; i < common.Count && !hasOverlappingPos; i++)
                for (int j = i + 1; j < common.Count; j++)
                    if (common[i].Pos.Any(p => common[j].Pos.Contains(p)))
                    {
                        hasOverlappingPos = true;
                        break;
                    }

            if (!hasOverlappingPos) continue;

            ambiguousSurfaces++;
            if (examples.Count < 50)
            {
                examples.Add((lookup.Surface, common.SelectMany(e => e.Readings).Distinct().Count(),
                    common.Select(e => (e.WordId, string.Join("/", e.Readings), string.Join(",", e.Pos))).ToList()));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"=== Results ===");
        Console.WriteLine($"Total surfaces with 2+ WordIds:       {totalMultiEntry:N0}");
        Console.WriteLine($"Ambiguous (different reading + POS):   {ambiguousSurfaces:N0}");
        Console.WriteLine($"Non-ambiguous (same reading or no POS overlap): {totalMultiEntry - ambiguousSurfaces:N0}");
        Console.WriteLine();

        if (examples.Count > 0)
        {
            Console.WriteLine($"Examples (first {examples.Count}):");
            foreach (var (surface, readingCount, entries) in examples)
            {
                Console.WriteLine($"  {surface} ({readingCount} readings):");
                foreach (var (wid, reading, pos) in entries)
                    Console.WriteLine($"    {wid}: {reading} [{pos}]");
            }
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
        Console.WriteLine("PerFormPriorities:");
        foreach (var f in orderedForms)
            Console.WriteLine($"  [{f.ReadingIndex}] {f.Text} → [{string.Join(", ", f.Priorities ?? [])}]");
        Console.WriteLine($"Origin: {word.Origin}");

        if (word.Definitions?.Count > 0)
        {
            Console.WriteLine($"Definitions ({word.Definitions.Count}):");
            foreach (var def in word.Definitions)
            {
                var posStr = def.PartsOfSpeech.Count > 0 ? string.Join(", ", def.PartsOfSpeech) : "";
                var miscStr = def.Misc != null && def.Misc.Count > 0 ? $" misc=[{string.Join(",", def.Misc)}]" : "";
                var meanings = def.EnglishMeanings.Count > 0 ? string.Join("; ", def.EnglishMeanings) : "(no English)";
                Console.WriteLine($"  SI={def.SenseIndex} [{posStr}]{miscStr} {meanings}");
            }
        }
    }
}
