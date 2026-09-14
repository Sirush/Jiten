using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Jiten.Core.Data;
using Jiten.Parser;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Cli.Commands;

/// <summary>Dictionary-update regression harness: parse a fixed deck corpus before and after a JMdict/JMnedict sync and report what moved.</summary>
public class DictDiffCommands(CliContext context)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public class Snapshot
    {
        public DateTime TakenAt { get; set; }
        public List<SnapDeck> Decks { get; set; } = [];
        public Dictionary<int, SnapEntry> Entries { get; set; } = new();
        public Dictionary<int, SnapGloss> Glosses { get; set; } = new();
        public Dictionary<int, int> WordRanks { get; set; } = new();
        public Dictionary<string, int> FormRanks { get; set; } = new();
    }

    public class SnapDeck
    {
        public int DeckId { get; set; }
        public string Title { get; set; } = "";
        public string MediaType { get; set; } = "";
        public int Chars { get; set; }
        public int WordCount { get; set; }
        public int UniqueWords { get; set; }
        public List<SnapRow> Rows { get; set; } = [];
        public Dictionary<string, string> Context { get; set; } = new();
    }

    /// <summary>One (surface, WordId, ReadingIndex) triple and how often it was produced in the deck.</summary>
    public class SnapRow
    {
        public string S { get; set; } = "";
        public int W { get; set; }
        public byte R { get; set; }
        public int O { get; set; }
    }

    public class SnapEntry
    {
        public List<string> Pos { get; set; } = [];
        public List<SnapForm> Forms { get; set; } = [];
    }

    public class SnapForm
    {
        public short I { get; set; }
        public string T { get; set; } = "";
        public string? Ru { get; set; }
        public bool A { get; set; }
    }

    public class SnapGloss
    {
        public List<SnapSense> Senses { get; set; } = [];
    }

    public class SnapSense
    {
        public List<string> Pos { get; set; } = [];
        public List<string> Misc { get; set; } = [];
        public List<string> Glosses { get; set; } = [];
    }

    public static List<int> ParseDeckIds(string? raw) =>
        (raw ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Select(int.Parse).Distinct().ToList();

    // ---------------------------------------------------------------- orchestration

    public async Task RunFullFlow(CliOptions options, ImportCommands importCommands)
    {
        var outDir = options.DictDiff!;
        var deckIds = ParseDeckIds(options.DictDiffDecks);
        if (deckIds.Count == 0)
        {
            Console.WriteLine("--dict-diff needs --dict-diff-decks 1,2,3 (deck ids to parse before and after the sync).");
            return;
        }

        var missing = new List<string>();
        if (string.IsNullOrEmpty(options.XmlPath)) missing.Add("--xml");
        if (string.IsNullOrEmpty(options.DictionaryPath)) missing.Add("--dic");
        if (string.IsNullOrEmpty(options.FuriganaPath)) missing.Add("--furi");
        if (string.IsNullOrEmpty(options.SyncJMNedict)) missing.Add("--sync-jmnedict <JMnedict.xml>");
        if (missing.Count > 0)
        {
            Console.WriteLine($"--dict-diff runs the JMdict and JMnedict sync itself and needs: {string.Join(", ", missing)}");
            return;
        }

        Directory.CreateDirectory(outDir);
        var beforePath = Path.Combine(outDir, "before.json");
        var afterPath = Path.Combine(outDir, "after.json");
        var reportPath = Path.Combine(outDir, "report.html");
        var deckArg = string.Join(",", deckIds);
        var total = Stopwatch.StartNew();

        Console.WriteLine($"=== dict-diff: {deckIds.Count} decks, output {outDir}");
        Console.WriteLine();
        Console.WriteLine("=== Step 1/4: snapshot before sync (fresh process: flush redis, warm jmdict cache, parse corpus)");
        await RunChild(["--flush-redis", "--warm-jmdict-cache", "--dict-diff-snapshot", beforePath, "--dict-diff-decks", deckArg]);

        Console.WriteLine();
        Console.WriteLine("=== Step 2/4: sync JMdict then JMnedict");
        // Both syncs rewrite the ~7,300 name entries JMdict shares with JMnedict; JMnedict must win so they keep name-type POS.
        await importCommands.SyncJmDict(options);
        await importCommands.SyncJMNedict(options);

        Console.WriteLine();
        Console.WriteLine("=== Step 3/4: snapshot after sync (fresh process: flush redis, warm jmdict cache, parse corpus)");
        await RunChild(["--flush-redis", "--warm-jmdict-cache", "--dict-diff-snapshot", afterPath, "--dict-diff-decks", deckArg]);

        Console.WriteLine();
        Console.WriteLine("=== Step 4/4: report");
        await WriteReport(beforePath, afterPath, reportPath);
        Console.WriteLine($"Done in {total.Elapsed.TotalMinutes:N1} min. Open {reportPath}");
    }

    // The parser holds dictionary lookups in static state, so each snapshot runs in its own process.
    private static async Task RunChild(IEnumerable<string> args)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the running CLI executable.");
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Directory.GetCurrentDirectory(),
            UseShellExecute = false,
        };
        // Started as "dotnet Jiten.Cli.dll": the host is dotnet itself, so the dll has to be passed again.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_gcServer"] = "1";

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start child CLI process.");
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Child CLI process exited with code {proc.ExitCode}: {string.Join(" ", args)}");
    }

    // ---------------------------------------------------------------- snapshot

    public async Task TakeSnapshot(CliOptions options)
    {
        var deckIds = ParseDeckIds(options.DictDiffDecks);
        if (deckIds.Count == 0)
        {
            Console.WriteLine("--dict-diff-snapshot needs --dict-diff-decks 1,2,3");
            return;
        }

        var sw = Stopwatch.StartNew();
        var snapshot = new Snapshot { TakenAt = DateTime.UtcNow };

        await using var db = await context.ContextFactory.CreateDbContextAsync();
        var decks = await db.Decks.AsNoTracking()
                            .Include(d => d.RawText)
                            .Include(d => d.Children).ThenInclude(c => c.RawText)
                            .Include(d => d.DictionaryEntries)
                            .Where(d => deckIds.Contains(d.DeckId))
                            .ToListAsync();

        foreach (var id in deckIds.Where(id => decks.All(d => d.DeckId != id)))
            Console.WriteLine($"  deck {id}: not found, skipped");

        foreach (var deck in decks.OrderBy(d => deckIds.IndexOf(d.DeckId)))
        {
            var dictEntries = deck.DictionaryEntries.Count > 0
                ? deck.DictionaryEntries.ToList()
                : deck.ParentDeckId != null
                    ? await db.DeckDictionaryEntries.AsNoTracking().Where(e => e.DeckId == deck.ParentDeckId).ToListAsync()
                    : null;

            var units = deck.Children.Count == 0
                ? [(deck, deck.OriginalTitle)]
                : deck.Children.OrderBy(c => c.DeckOrder).Select(c => (c, $"{deck.OriginalTitle} / {c.OriginalTitle}")).ToList();

            var texts = new List<string>();
            var labels = new List<(Deck deck, string title)>();
            foreach (var (unit, title) in units)
            {
                if (unit.RawText == null)
                {
                    Console.WriteLine($"  deck {unit.DeckId} ({title}): no raw text, skipped");
                    continue;
                }
                texts.Add(unit.RawText.RawText);
                labels.Add((unit, title));
            }
            if (texts.Count == 0) continue;

            var deckSw = Stopwatch.StartNew();
            var sink = new List<List<ParsedOccurrence>>();
            var parsed = await Jiten.Parser.Parser.ParseTextsToDeck(context.ContextFactory, texts, storeRawText: false,
                                                                    predictDifficulty: false, deck.MediaType,
                                                                    dictionaryEntries: dictEntries, occurrenceSink: sink);

            for (int i = 0; i < parsed.Count; i++)
                snapshot.Decks.Add(BuildSnapDeck(labels[i].deck, labels[i].title, parsed[i], sink[i], texts[i]));

            Console.WriteLine($"  deck {deck.DeckId} ({deck.OriginalTitle}): {texts.Count} text(s), {parsed.Sum(p => p.WordCount):N0} words in {deckSw.ElapsedMilliseconds:N0} ms");
        }

        Console.WriteLine("  loading dictionary snapshot...");
        await LoadDictionary(db, snapshot);

        Console.WriteLine("  loading glosses and ranks for words in the corpus...");
        var usedIds = snapshot.Decks.SelectMany(d => d.Rows.Select(r => r.W)).Distinct().ToList();
        await LoadGlosses(db, snapshot, usedIds);

        var path = options.DictDiffSnapshot!;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using (var fs = File.Create(path))
            await JsonSerializer.SerializeAsync(fs, snapshot, JsonOptions);

        Console.WriteLine($"Snapshot written to {path} ({new FileInfo(path).Length / (1024 * 1024)} MB, {snapshot.Decks.Count} texts, {snapshot.Entries.Count:N0} dictionary entries) in {sw.Elapsed.TotalSeconds:N0} s");
    }

    private static SnapDeck BuildSnapDeck(Deck source, string title, Deck parsed, List<ParsedOccurrence> occurrences, string rawText)
    {
        var counts = new Dictionary<(string, int, byte), int>();
        foreach (var o in occurrences)
        {
            var key = (o.Surface, o.WordId, o.ReadingIndex);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var snap = new SnapDeck
        {
            DeckId = source.DeckId,
            Title = title,
            MediaType = source.MediaType.ToString(),
            Chars = parsed.CharacterCount,
            WordCount = parsed.WordCount,
            UniqueWords = parsed.UniqueWordCount,
            Rows = counts.OrderByDescending(kv => kv.Value)
                         .Select(kv => new SnapRow { S = kv.Key.Item1, W = kv.Key.Item2, R = kv.Key.Item3, O = kv.Value })
                         .ToList(),
        };

        if (parsed.ExampleSentences != null)
        {
            foreach (var sentence in parsed.ExampleSentences)
            foreach (var w in sentence.Words)
                snap.Context.TryAdd($"{w.WordId}:{w.ReadingIndex}", sentence.Text);
        }

        var flat = rawText.Replace("\r", "").Replace("\n", "");
        foreach (var group in snap.Rows.GroupBy(r => (r.W, r.R)))
        {
            var key = $"{group.Key.W}:{group.Key.R}";
            if (snap.Context.ContainsKey(key)) continue;
            var surface = group.OrderByDescending(r => r.O).First().S;
            if (surface.Length == 0) continue;
            var idx = flat.IndexOf(surface, StringComparison.Ordinal);
            if (idx < 0) continue;
            var start = Math.Max(0, idx - 30);
            var end = Math.Min(flat.Length, idx + surface.Length + 30);
            snap.Context[key] = (start > 0 ? "…" : "") + flat[start..end] + (end < flat.Length ? "…" : "");
        }

        return snap;
    }

    private static async Task LoadDictionary(Core.JitenDbContext db, Snapshot snapshot)
    {
        var words = await db.JMDictWords.AsNoTracking()
                            .Select(w => new { w.WordId, w.PartsOfSpeech })
                            .ToListAsync();
        foreach (var w in words)
            snapshot.Entries[w.WordId] = new SnapEntry { Pos = w.PartsOfSpeech };

        var forms = await db.WordForms.AsNoTracking()
                            .OrderBy(f => f.WordId).ThenBy(f => f.ReadingIndex)
                            .Select(f => new { f.WordId, f.ReadingIndex, f.Text, f.RubyText, f.IsActiveInLatestSource })
                            .ToListAsync();
        foreach (var f in forms)
        {
            if (!snapshot.Entries.TryGetValue(f.WordId, out var entry)) continue;
            entry.Forms.Add(new SnapForm
            {
                I = f.ReadingIndex, T = f.Text, A = f.IsActiveInLatestSource,
                Ru = string.IsNullOrEmpty(f.RubyText) || f.RubyText == f.Text ? null : f.RubyText,
            });
        }
    }

    private static async Task LoadGlosses(Core.JitenDbContext db, Snapshot snapshot, List<int> wordIds)
    {
        const int senseLimit = 3, glossLimit = 4;
        foreach (var chunk in wordIds.Chunk(20000))
        {
            var ids = chunk.ToList();
            var defs = await db.Definitions.AsNoTracking()
                               .Where(d => ids.Contains(d.WordId))
                               .OrderBy(d => d.WordId).ThenBy(d => d.SenseIndex)
                               .Select(d => new { d.WordId, d.Pos, d.PartsOfSpeech, d.Misc, d.EnglishMeanings })
                               .ToListAsync();
            foreach (var d in defs)
            {
                if (!snapshot.Glosses.TryGetValue(d.WordId, out var g))
                    snapshot.Glosses[d.WordId] = g = new SnapGloss();
                if (g.Senses.Count >= senseLimit) continue;
                g.Senses.Add(new SnapSense
                {
                    Pos = d.Pos.Count > 0 ? d.Pos : d.PartsOfSpeech,
                    Misc = d.Misc,
                    Glosses = d.EnglishMeanings.Take(glossLimit).ToList(),
                });
            }

            var ranks = await db.JmDictWordFrequencies.AsNoTracking()
                                .Where(f => ids.Contains(f.WordId))
                                .Select(f => new { f.WordId, f.FrequencyRank })
                                .ToListAsync();
            foreach (var r in ranks) snapshot.WordRanks[r.WordId] = r.FrequencyRank;

            var formRanks = await db.WordFormFrequencies.AsNoTracking()
                                    .Where(f => ids.Contains(f.WordId))
                                    .Select(f => new { f.WordId, f.ReadingIndex, f.FrequencyRank })
                                    .ToListAsync();
            foreach (var r in formRanks) snapshot.FormRanks[$"{r.WordId}:{r.ReadingIndex}"] = r.FrequencyRank;
        }
    }

    // ---------------------------------------------------------------- report

    public async Task WriteReport(string beforePath, string afterPath, string reportPath)
    {
        var sw = Stopwatch.StartNew();
        var before = await LoadSnapshot(beforePath);
        var after = await LoadSnapshot(afterPath);
        Console.WriteLine($"  loaded snapshots in {sw.ElapsedMilliseconds:N0} ms");

        var html = DictDiffReport.Build(before, after, beforePath, afterPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, html, Encoding.UTF8);
        Console.WriteLine($"Report written to {reportPath} in {sw.Elapsed.TotalSeconds:N1} s");
    }

    private static async Task<Snapshot> LoadSnapshot(string path)
    {
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<Snapshot>(fs, JsonOptions)
               ?? throw new InvalidOperationException($"Empty snapshot: {path}");
    }
}
