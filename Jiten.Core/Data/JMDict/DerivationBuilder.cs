using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.Data.JMDict;

public class DerivationCategoryStats
{
    public int Pairs { get; set; }
    public int Rows { get; set; }
    public int Bidirectional { get; set; }
    public int OneWay { get; set; }
    public int DroppedByRule { get; set; }
    public int DroppedByOverride { get; set; }
    public int Recategorized { get; set; }
    public int NeedsReview { get; set; }
}

public class DerivationBuildReport
{
    public Dictionary<DerivationCategory, DerivationCategoryStats> Categories { get; } = new();
    public int TotalPairs { get; set; }
    public int TotalRows { get; set; }
    public int PairsAdded { get; set; }
    public int PairsRemoved { get; set; }
    public int StaleOverrides { get; set; }
    public int OverrideCount { get; set; }
}

/// <summary>Rebuilds <c>jmdict.WordDerivations</c>. The table is a pure function of the dictionary plus the
/// committed override file, so truncate-and-regenerate is always safe.</summary>
public static partial class DerivationBuilder
{
    private const int JmDictWordIdLimit = 5_000_000;

    private sealed class FormEntry
    {
        public byte ReadingIndex;
        public string Text = "";
        public bool IsKanji;
    }

    private sealed class SenseEntry
    {
        public string[] Pos = [];
        public short[]? Restrict;
        public string[] Glosses = [];
    }

    private sealed class WordEntry
    {
        public int WordId;
        public string[] EntryPos = [];
        public List<FormEntry> Forms = [];
        public List<SenseEntry> Senses = [];
        public int FrequencyRank;
        private Dictionary<byte, HashSet<string>>? _posByReading;

        public HashSet<string> PosForReading(byte readingIndex)
        {
            _posByReading ??= new Dictionary<byte, HashSet<string>>();
            if (_posByReading.TryGetValue(readingIndex, out var cached))
                return cached;

            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sense in Senses)
            {
                if (sense.Restrict is { Length: > 0 } && !sense.Restrict.Contains(readingIndex))
                    continue;
                foreach (var pos in sense.Pos)
                    result.Add(pos);
            }

            if (result.Count == 0)
                foreach (var pos in EntryPos)
                    result.Add(pos);

            _posByReading[readingIndex] = result;
            return result;
        }

        public bool HasKanjiForm => Forms.Exists(f => f.IsKanji);
    }

    private sealed class Pair
    {
        public required WordEntry Base;
        public required WordEntry Derived;

        /// <summary>The category the transform rule produced. Overrides are keyed by it, so it survives the
        /// split and any Recategorize verdict, unlike <see cref="Category"/>.</summary>
        public required DerivationCategory RuleCategory;

        public DerivationCategory Category;
        /// <summary>Form indexes the transform actually matched, kept paired: unioning the two sides separately
        /// would emit combinations no rule ever produced, letting one reading cover another entry's.</summary>
        public readonly HashSet<(byte Base, byte Derived)> KanjiMatches = [];

        public readonly HashSet<(byte Base, byte Derived)> KanaMatches = [];
        public DerivationDirection Direction = DerivationDirection.Bidirectional;
        public DerivationSource Source = DerivationSource.RuleGenerated;
        public string Outcome = "";

        public bool Analysed;
        public DerivationVerdict AutoVerdict;
        public string AutoSignal = "";
        public bool HasOverride;
    }

    public static async Task<DerivationBuildReport> Build(IDbContextFactory<JitenDbContext> contextFactory,
                                                          bool dryRun = false, string? dumpPath = null,
                                                          string? classifyPath = null)
    {
        var report = new DerivationBuildReport();

        Console.WriteLine("Loading dictionary data...");
        var words = await LoadWords(contextFactory);
        var index = BuildTextIndex(words);
        Console.WriteLine($"  {words.Count} JMdict entries, {index.Count} distinct surfaces.");

        Console.WriteLine("Generating candidates...");
        var pairs = GenerateCandidates(words, index);
        Console.WriteLine($"  {pairs.Count} structurally-gated pairs.");

        await LoadGlosses(contextFactory, words, pairs);

        // Only a dry run may proceed without the overrides: writing the table without them would ship the
        // recategorized mislabels (育つ→育てる and friends) as live Potential links.
        DerivationOverrideSet overrides;
        try
        {
            overrides = DerivationOverrideSet.Load();
        }
        catch (DerivationOverrideSet.MissingOverrideFileException) when (dryRun)
        {
            Console.WriteLine("  WARNING: overrides file missing; dry run continues on automatic verdicts alone.");
            overrides = new DerivationOverrideSet();
        }

        report.OverrideCount = overrides.Count;
        Console.WriteLine($"  {overrides.Count} committed overrides loaded.");
        if (overrides.LegacyRecategorizeCount > 0)
            Console.WriteLine($"  WARNING: {overrides.LegacyRecategorizeCount} overrides still carry the legacy " +
                              "\"recategorize\" field, which the loader ignores.");

        // Computed before gating: an override that excludes a pair is doing its job, not going stale.
        var candidateKeys = pairs.Select(p => (p.Base.WordId, p.Derived.WordId, p.Category)).ToHashSet();
        report.StaleOverrides = overrides.Keys.Count(k => !candidateKeys.Contains(k));

        var kept = ApplyVerdicts(pairs, overrides, report);
        Console.WriteLine($"  {kept.Count} pairs survive gating.");

        var rows = MaterializeRows(kept);
        report.TotalPairs = kept.Count;
        report.TotalRows = rows.Count;

        foreach (var pair in kept)
        {
            var stats = Stats(report, pair.Category);
            stats.Pairs++;
            if (pair.Direction == DerivationDirection.Bidirectional) stats.Bidirectional++;
            else stats.OneWay++;
        }

        foreach (var row in rows)
            Stats(report, row.Category).Rows++;

        if (dumpPath != null)
            WriteDump(dumpPath, pairs);

        if (classifyPath != null)
            Console.WriteLine($"  {WriteClassificationInput(classifyPath, pairs)} pairs written for classification.");

        if (dryRun)
        {
            Console.WriteLine("Dry run: table not modified.");
            return report;
        }

        await WriteTable(contextFactory, rows, kept, report);
        return report;
    }

    public static void PrintSummary(DerivationBuildReport report)
    {
        Console.WriteLine();
        Console.WriteLine("=== WordDerivations ===");
        Console.WriteLine($"{"category",-22} {"pairs",6} {"rows",6} {"bidir",6} {"1-way",6} {"drop",6} {"ovr",5} {"recat",6} {"review",6}");

        foreach (var (category, stats) in report.Categories.OrderByDescending(c => c.Value.Pairs))
        {
            Console.WriteLine($"{DerivationCategories.GetKey(category),-22} {stats.Pairs,6} {stats.Rows,6} " +
                              $"{stats.Bidirectional,6} {stats.OneWay,6} {stats.DroppedByRule,6} " +
                              $"{stats.DroppedByOverride,5} {stats.Recategorized,6} {stats.NeedsReview,6}");
        }

        Console.WriteLine($"TOTAL {report.TotalPairs} pairs, {report.TotalRows} rows " +
                          $"(+{report.PairsAdded} / -{report.PairsRemoved} pairs vs previous build)");
        Console.WriteLine($"Overrides: {report.OverrideCount} loaded, {report.StaleOverrides} no longer match any pair.");
    }

    private static DerivationCategoryStats Stats(DerivationBuildReport report, DerivationCategory category)
    {
        if (!report.Categories.TryGetValue(category, out var stats))
            report.Categories[category] = stats = new DerivationCategoryStats();
        return stats;
    }

    private static async Task<Dictionary<int, WordEntry>> LoadWords(IDbContextFactory<JitenDbContext> contextFactory)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        context.Database.SetCommandTimeout(600);

        var words = new Dictionary<int, WordEntry>();

        var wordRows = await context.JMDictWords
                                    .Where(w => w.WordId < JmDictWordIdLimit)
                                    .Select(w => new { w.WordId, w.PartsOfSpeech })
                                    .ToListAsync();

        foreach (var row in wordRows)
            words[row.WordId] = new WordEntry { WordId = row.WordId, EntryPos = row.PartsOfSpeech.ToArray() };

        var formRows = await context.WordForms
                                    .Where(f => f.WordId < JmDictWordIdLimit && f.IsActiveInLatestSource &&
                                                !f.IsSearchOnly && !f.IsObsolete)
                                    .Select(f => new { f.WordId, f.ReadingIndex, f.Text, f.FormType })
                                    .ToListAsync();

        foreach (var row in formRows)
        {
            if (row.ReadingIndex is < 0 or > byte.MaxValue) continue;
            if (!words.TryGetValue(row.WordId, out var word)) continue;
            word.Forms.Add(new FormEntry
            {
                ReadingIndex = (byte)row.ReadingIndex,
                Text = row.Text,
                IsKanji = row.FormType == JmDictFormType.KanjiForm
            });
        }

        var senseRows = await context.Definitions
                                     .Where(d => d.WordId < JmDictWordIdLimit && d.IsActiveInLatestSource)
                                     .Select(d => new { d.WordId, d.Pos, d.PartsOfSpeech, d.RestrictedToReadingIndices })
                                     .ToListAsync();

        foreach (var row in senseRows)
        {
            if (!words.TryGetValue(row.WordId, out var word)) continue;
            var pos = row.Pos.Count > 0 ? row.Pos : row.PartsOfSpeech;
            word.Senses.Add(new SenseEntry
            {
                Pos = pos.ToArray(),
                Restrict = row.RestrictedToReadingIndices?.ToArray()
            });
        }

        var frequencyRows = await context.JmDictWordFrequencies
                                         .Where(f => f.WordId < JmDictWordIdLimit)
                                         .Select(f => new { f.WordId, f.FrequencyRank })
                                         .ToListAsync();

        foreach (var row in frequencyRows)
            if (words.TryGetValue(row.WordId, out var word))
                word.FrequencyRank = row.FrequencyRank;

        return words;
    }

    /// <summary>Glosses are only needed for the entries that reached a candidate pair, so they load last.</summary>
    private static async Task LoadGlosses(IDbContextFactory<JitenDbContext> contextFactory,
                                          Dictionary<int, WordEntry> words, List<Pair> pairs)
    {
        var wordIds = new HashSet<int>();
        foreach (var pair in pairs)
        {
            wordIds.Add(pair.Base.WordId);
            wordIds.Add(pair.Derived.WordId);
        }

        if (wordIds.Count == 0) return;

        await using var context = await contextFactory.CreateDbContextAsync();
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        context.Database.SetCommandTimeout(600);

        var idList = wordIds.ToList();
        var glossesByWord = new Dictionary<int, List<(string[] Pos, short[]? Restrict, string[] Glosses)>>();

        const int batch = 20000;
        for (var i = 0; i < idList.Count; i += batch)
        {
            var slice = idList.Skip(i).Take(batch).ToList();
            var rows = await context.Definitions
                                    .Where(d => slice.Contains(d.WordId) && d.IsActiveInLatestSource)
                                    .Select(d => new
                                    {
                                        d.WordId, d.Pos, d.PartsOfSpeech, d.RestrictedToReadingIndices, d.EnglishMeanings
                                    })
                                    .ToListAsync();

            foreach (var row in rows)
            {
                if (!glossesByWord.TryGetValue(row.WordId, out var list))
                    glossesByWord[row.WordId] = list = [];
                var pos = row.Pos.Count > 0 ? row.Pos : row.PartsOfSpeech;
                list.Add((pos.ToArray(), row.RestrictedToReadingIndices?.ToArray(), row.EnglishMeanings.ToArray()));
            }
        }

        foreach (var (wordId, senses) in glossesByWord)
        {
            if (!words.TryGetValue(wordId, out var word)) continue;
            word.Senses = senses
                          .Select(s => new SenseEntry { Pos = s.Pos, Restrict = s.Restrict, Glosses = s.Glosses })
                          .ToList();
        }
    }

    private static Dictionary<string, List<(int WordId, byte ReadingIndex, bool IsKanji)>> BuildTextIndex(
        Dictionary<int, WordEntry> words)
    {
        var index = new Dictionary<string, List<(int, byte, bool)>>(StringComparer.Ordinal);
        foreach (var word in words.Values)
        {
            foreach (var form in word.Forms)
            {
                if (!index.TryGetValue(form.Text, out var list))
                    index[form.Text] = list = [];
                list.Add((word.WordId, form.ReadingIndex, form.IsKanji));
            }
        }

        return index;
    }

    private static List<Pair> GenerateCandidates(Dictionary<int, WordEntry> words,
                                                  Dictionary<string, List<(int WordId, byte ReadingIndex, bool IsKanji)>> index)
    {
        var pairs = new Dictionary<(int, int, DerivationCategory), Pair>();

        foreach (var rule in DerivationRules.All)
        {
            var basePos = rule.BasePos.ToHashSet(StringComparer.Ordinal);
            var derivedPos = rule.DerivedPos.ToHashSet(StringComparer.Ordinal);

            foreach (var word in words.Values)
            {
                var kanjiHits = MatchSide(word, rule, basePos, derivedPos, words, index, kanjiSide: true);
                if (kanjiHits.Count == 0 && word.HasKanjiForm) continue;

                var kanaHits = MatchSide(word, rule, basePos, derivedPos, words, index, kanjiSide: false);
                if (kanaHits.Count == 0) continue;

                // Dual-surface gate: a kanji-bearing base links only when the kanji and kana transforms agree
                // on one entry. Kana-only bases have no homograph risk, but may only reach kana-only entries.
                var targets = word.HasKanjiForm
                    ? kanaHits.Keys.Where(kanjiHits.ContainsKey)
                    : kanaHits.Keys.Where(id => words.TryGetValue(id, out var d) && !d.HasKanjiForm);

                foreach (var targetId in targets.ToList())
                {
                    if (targetId == word.WordId) continue;
                    var derived = words[targetId];

                    var key = (word.WordId, targetId, rule.Category);
                    if (!pairs.TryGetValue(key, out var pair))
                        pairs[key] = pair = new Pair
                        {
                            Base = word, Derived = derived,
                            Category = rule.Category, RuleCategory = rule.Category
                        };

                    if (kanjiHits.TryGetValue(targetId, out var kanjiMatch))
                        pair.KanjiMatches.UnionWith(kanjiMatch);

                    pair.KanaMatches.UnionWith(kanaHits[targetId]);
                }
            }
        }

        return pairs.Values.Where(p => p.KanjiMatches.Count + p.KanaMatches.Count > 0).ToList();
    }

    private static Dictionary<int, HashSet<(byte Base, byte Derived)>> MatchSide(
        WordEntry word, DerivationRules.Rule rule, HashSet<string> basePos, HashSet<string> derivedPos,
        Dictionary<int, WordEntry> words,
        Dictionary<string, List<(int WordId, byte ReadingIndex, bool IsKanji)>> index, bool kanjiSide)
    {
        var hits = new Dictionary<int, HashSet<(byte Base, byte Derived)>>();

        foreach (var form in word.Forms)
        {
            if (form.IsKanji != kanjiSide) continue;

            var formPos = word.PosForReading(form.ReadingIndex);
            if (!formPos.Overlaps(basePos)) continue;

            foreach (var candidate in rule.Transform(formPos, form.Text, kanjiSide))
            {
                if (candidate == form.Text) continue;
                if (!index.TryGetValue(candidate, out var matches)) continue;

                foreach (var match in matches)
                {
                    if (match.IsKanji != kanjiSide) continue;
                    if (match.WordId == word.WordId) continue;
                    if (!hits.TryGetValue(match.WordId, out var entry))
                        hits[match.WordId] = entry = [];
                    entry.Add((form.ReadingIndex, match.ReadingIndex));
                }
            }
        }

        // Sense-level POS gate on the target: a derived index only counts when a sense that applies to that
        // reading carries the category's target tag.
        foreach (var (wordId, entry) in hits.ToList())
        {
            entry.RemoveWhere(m => !words.TryGetValue(wordId, out var target) ||
                                   !target.PosForReading(m.Derived).Overlaps(derivedPos));
            if (entry.Count == 0)
                hits.Remove(wordId);
        }

        return hits;
    }

    private static List<Pair> ApplyVerdicts(List<Pair> pairs, DerivationOverrideSet overrides,
                                             DerivationBuildReport report)
    {
        var kept = new List<Pair>();
        var potentialPairs = pairs
                             .Where(p => p.Category == DerivationCategory.Potential)
                             .Select(p => Unordered(p.Base.WordId, p.Derived.WordId))
                             .ToHashSet();

        foreach (var pair in pairs)
        {
            var stats = Stats(report, pair.Category);

            if (pair.Category == DerivationCategory.CausativeDoublet &&
                potentialPairs.Contains(Unordered(pair.Base.WordId, pair.Derived.WordId)))
            {
                stats.DroppedByRule++;
                pair.Outcome = "DroppedAsPotential";
                continue;
            }

            var analysis = SenseCoverage.Analyse(pair.Base, pair.Derived, pair.Category);
            pair.Outcome = analysis.Verdict.ToString();
            pair.Analysed = true;
            pair.AutoVerdict = analysis.Verdict;
            pair.AutoSignal = analysis.Signal;

            if (analysis.NeedsReview)
                stats.NeedsReview++;

            var category = pair.Category;

            // A godan→eru pair whose target is intransitive against a transitive base and shows no
            // ability gloss is a lexical transitivity pair, not a potential.
            if (category == DerivationCategory.Potential && analysis.IsTransitivitySplit)
            {
                category = DerivationCategory.TransitivityPair;
                pair.Source = DerivationSource.Curated;
                pair.Outcome = "SplitToTransitivityPair";
                stats.Recategorized++;
            }

            var over = FindOverride(overrides, pair, category);
            pair.HasOverride = over != null;

            var (dropped, direction, gatedCategory) = ResolveGate(analysis.Verdict, category, over);

            if (dropped)
            {
                if (over != null)
                {
                    stats.DroppedByOverride++;
                    pair.Outcome = "OverrideExclude";
                }
                else
                {
                    stats.DroppedByRule++;
                }

                continue;
            }

            if (over != null)
            {
                if (over.Verdict == DerivationVerdict.Recategorize)
                    stats.Recategorized++;
                pair.Source = DerivationSource.Manual;
                pair.Outcome = "Override" + over.Verdict;
            }

            pair.Direction = direction;
            pair.Category = gatedCategory;
            kept.Add(pair);
        }

        return kept;
    }

    /// <summary>An override may be keyed by either category, so a pair the split moved still matches.</summary>
    private static DerivationOverride? FindOverride(DerivationOverrideSet overrides, Pair pair,
                                                     DerivationCategory category)
        => overrides.TryGet(pair.Base.WordId, pair.Derived.WordId, category, out var direct) ? direct
            : overrides.TryGet(pair.Base.WordId, pair.Derived.WordId, pair.RuleCategory, out var byRule) ? byRule
                : null;

    /// <summary>With no committed override the automatic verdict binds, in every category rather than a guarded few.</summary>
    internal static (bool Dropped, DerivationDirection Direction, DerivationCategory Category) ResolveGate(
        DerivationVerdict autoVerdict, DerivationCategory category, DerivationOverride? over)
    {
        if (over == null)
            return autoVerdict switch
            {
                DerivationVerdict.Exclude => (true, DerivationDirection.Bidirectional, category),
                DerivationVerdict.OneWayOnly => (false, DerivationDirection.BaseToDerivedOnly, category),
                _ => (false, DerivationDirection.Bidirectional, category)
            };

        return over.Verdict switch
        {
            DerivationVerdict.Exclude => (true, DerivationDirection.Bidirectional, category),
            DerivationVerdict.OneWayOnly => (false, DerivationDirection.BaseToDerivedOnly, category),
            DerivationVerdict.Recategorize => (false, over.Direction ?? DerivationDirection.Bidirectional,
                                               over.NewCategory ?? category),
            _ => (false, DerivationDirection.Bidirectional, category)
        };
    }

    private static (int, int) Unordered(int a, int b) => a < b ? (a, b) : (b, a);

    private static List<JmDictWordDerivation> MaterializeRows(List<Pair> pairs)
    {
        var rows = new List<JmDictWordDerivation>();
        var seen = new HashSet<(int, byte, int, byte, DerivationCategory)>();

        foreach (var pair in pairs)
        {
            // Same-script rows keep the index pairing the transform produced.
            foreach (var (baseIndex, derivedIndex) in pair.KanjiMatches)
                Add(baseIndex, derivedIndex);

            foreach (var (baseIndex, derivedIndex) in pair.KanaMatches)
                Add(baseIndex, derivedIndex);

            // Form closure: a kanji base index also covers the derived entry's kana readings (they spell the
            // same word), while a kana base index stays on kana so kana knowledge never confers a kanji form.
            foreach (var baseIndex in pair.KanjiMatches.Select(m => m.Base).Distinct())
            foreach (var derivedIndex in pair.KanaMatches.Select(m => m.Derived).Distinct())
                Add(baseIndex, derivedIndex);

            continue;

            void Add(byte baseIndex, byte derivedIndex)
            {
                if (!seen.Add((pair.Base.WordId, baseIndex, pair.Derived.WordId, derivedIndex, pair.Category)))
                    return;

                rows.Add(new JmDictWordDerivation
                {
                    BaseWordId = pair.Base.WordId,
                    BaseReadingIndex = baseIndex,
                    DerivedWordId = pair.Derived.WordId,
                    DerivedReadingIndex = derivedIndex,
                    Category = pair.Category,
                    Source = pair.Source,
                    Direction = pair.Direction
                });
            }
        }

        return rows;
    }

    private static async Task WriteTable(IDbContextFactory<JitenDbContext> contextFactory,
                                          List<JmDictWordDerivation> rows, List<Pair> kept,
                                          DerivationBuildReport report)
    {
        await using (var readContext = await contextFactory.CreateDbContextAsync())
        {
            var previous = await readContext.WordDerivations
                                            .AsNoTracking()
                                            .Select(d => new { d.BaseWordId, d.DerivedWordId, d.Category })
                                            .Distinct()
                                            .ToListAsync();

            var previousSet = previous.Select(p => (p.BaseWordId, p.DerivedWordId, p.Category)).ToHashSet();
            var currentSet = kept.Select(p => (p.Base.WordId, p.Derived.WordId, p.Category)).ToHashSet();
            report.PairsAdded = currentSet.Count(p => !previousSet.Contains(p));
            report.PairsRemoved = previousSet.Count(p => !currentSet.Contains(p));
        }

        // Truncate and inserts share one transaction: a crash must not leave production with an empty table.
        await using var writeContext = await contextFactory.CreateDbContextAsync();
        writeContext.Database.SetCommandTimeout(600);
        await using var transaction = await writeContext.Database.BeginTransactionAsync();

        await writeContext.Database.ExecuteSqlRawAsync(
            """TRUNCATE TABLE jmdict."WordDerivations" RESTART IDENTITY""");

        const int batch = 10000;
        for (var i = 0; i < rows.Count; i += batch)
        {
            writeContext.WordDerivations.AddRange(rows.Skip(i).Take(batch));
            await writeContext.SaveChangesAsync();
            writeContext.ChangeTracker.Clear();
        }

        await transaction.CommitAsync();
    }

    private static void WriteDump(string path, List<Pair> pairs)
    {
        var dump = pairs.Select(p => new
        {
            category = DerivationCategories.GetKey(p.Category),
            baseWordId = p.Base.WordId,
            baseText = p.Base.Forms.FirstOrDefault()?.Text,
            derivedWordId = p.Derived.WordId,
            derivedText = p.Derived.Forms.FirstOrDefault()?.Text,
            outcome = p.Outcome,
            direction = p.Direction.ToString()
        });

        File.WriteAllText(path, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }),
                          new UTF8Encoding(false));
    }

    /// <summary>
    /// Input for the one-time agent classification pass: the pairs the automatic rule wants to demote that no
    /// committed override has judged yet, beyond the frequency slice already classified. Frequency order puts
    /// the pairs that cost a learner most first; unranked entries sort last.
    /// </summary>
    private static int WriteClassificationInput(string path, List<Pair> pairs)
    {
        const int ClassifiedRankCeiling = 10000;

        var rows = pairs
                   .Where(p => p.Analysed && !p.HasOverride)
                   .Where(p => p.AutoVerdict is DerivationVerdict.Exclude or DerivationVerdict.OneWayOnly)
                   .Where(p => p.Derived.FrequencyRank <= 0 || p.Derived.FrequencyRank > ClassifiedRankCeiling)
                   .OrderBy(p => p.Derived.FrequencyRank <= 0)
                   .ThenBy(p => p.Derived.FrequencyRank)
                   .Select(p => new
                   {
                       baseWordId = p.Base.WordId,
                       baseText = Surface(p.Base, kanji: true),
                       baseKana = Surface(p.Base, kanji: false),
                       derivedWordId = p.Derived.WordId,
                       derivedText = Surface(p.Derived, kanji: true),
                       derivedKana = Surface(p.Derived, kanji: false),
                       category = DerivationCategories.GetKey(p.RuleCategory),
                       verdict = p.AutoVerdict.ToString(),
                       signal = p.AutoSignal,
                       derivedFrequencyRank = p.Derived.FrequencyRank,
                       baseFrequencyRank = p.Base.FrequencyRank,
                       baseGlosses = Glosses(p.Base),
                       derivedGlosses = Glosses(p.Derived)
                   })
                   .ToList();

        File.WriteAllText(path, JsonSerializer.Serialize(rows, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }), new UTF8Encoding(false));

        return rows.Count;
    }

    private static string Surface(WordEntry word, bool kanji)
    {
        var form = word.Forms.FirstOrDefault(f => f.IsKanji == kanji);
        return form?.Text ?? (kanji ? word.Forms.FirstOrDefault()?.Text ?? "" : "");
    }

    private static List<string> Glosses(WordEntry word)
        => word.Senses.Where(s => s.Glosses.Length > 0)
               .Select(s => string.Join(", ", s.Glosses))
               .ToList();
}
