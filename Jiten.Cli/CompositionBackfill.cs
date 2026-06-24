using System.Text;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Parser;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WanaKanaShaapu;

namespace Jiten.Cli;

/// <summary>
/// Segments JMDict compounds into component words using Sudachi Mode A (no-userdic) morphemes,
/// falling back to the per-kanji ruby split when Mode A returns the compound atomically, then
/// resolves each morpheme to a JMDict word with a sense-aware resolver
/// (kanji-exact candidates + frequency + name penalty + POS match + priority).
/// </summary>
public static class CompositionBackfill
{
    private static readonly HashSet<string> NamePos = new()
    {
        "person", "place", "station", "organization", "company", "surname",
        "name-fem", "name-masc", "product", "work", "given", "char", "group",
        "obj", "ev", "dei", "myth", "fict", "leg", "serv", "relig", "unclass"
    };

    private static readonly HashSet<string> MiscPos = new()
    {
        "col", "abbr", "arch", "obs", "rare", "uk", "yoji", "id", "proverb", "on-mim",
        "sl", "vulg", "derog", "hon", "hum", "pol", "fam", "male", "dated", "poet",
        "form", "euph", "obsc", "ok", "rk", "sk", "ik", "io", "gikun", "sens", "chn", "joc"
    };

    private static readonly Dictionary<char, string> Voice = new()
    {
        ['か'] = "が", ['き'] = "ぎ", ['く'] = "ぐ", ['け'] = "げ", ['こ'] = "ご",
        ['さ'] = "ざ", ['し'] = "じ", ['す'] = "ず", ['せ'] = "ぜ", ['そ'] = "ぞ",
        ['た'] = "だ", ['ち'] = "ぢ", ['つ'] = "づ", ['て'] = "で", ['と'] = "ど",
        ['は'] = "ばぱ", ['ひ'] = "びぴ", ['ふ'] = "ぶぷ", ['へ'] = "べぺ", ['ほ'] = "ぼぽ"
    };

    private sealed class ResolverData
    {
        public readonly Dictionary<int, string[]> Pos = new();
        public readonly Dictionary<int, bool> Prio = new();
        public readonly Dictionary<int, double> Freq = new();
        public readonly Dictionary<string, List<(int Wid, short Ridx)>> KanjiByText = new();
        public readonly Dictionary<string, List<(int Wid, short Ridx)>> KanaByText = new();
        public readonly Dictionary<int, HashSet<string>> Readings = new();
        public readonly Dictionary<(int, short), (string Text, string Ruby)> KanjiForm = new();
    }

    public static async Task Backfill(IDbContextFactory<JitenDbContext> contextFactory, bool dryRun, int dryRunSample)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        Console.WriteLine("Loading JMDict resolver data...");
        var data = await LoadData(context);
        Console.WriteLine($"Loaded {data.Pos.Count:N0} words, {data.KanjiForm.Count:N0} kanji forms.");

        Console.WriteLine("Loading gap targets (compounds with no composition rows)...");
        var targets = await LoadGapTargets(context, data, dryRun ? dryRunSample : 0);
        Console.WriteLine($"{targets.Count:N0} target compounds.");

        var stats = new Dictionary<string, int>
        {
            ["total"] = 0, ["modeA"] = 0, ["ruby"] = 0,
            ["drop_phrase"] = 0, ["drop_single"] = 0, ["drop_uncov"] = 0, ["drop_nameOnly"] = 0,
            ["drop_grammatical"] = 0, ["drop_inflTail"] = 0,
            ["nameLanding"] = 0, ["componentTotal"] = 0
        };
        var samples = new List<string>();
        var pending = new List<JmDictWordComposition>();

        const int chunk = 400;
        for (int start = 0; start < targets.Count; start += chunk)
        {
            var slice = targets.GetRange(start, Math.Min(chunk, targets.Count - start));
            var surfaces = slice.Select(t => t.Surface).ToList();
            var morphemeBatches = await Jiten.Parser.Parser.GetMorphemesBatch(contextFactory, surfaces);

            for (int i = 0; i < slice.Count; i++)
            {
                var (wid, ridx, surface) = slice[i];
                stats["total"]++;
                var morphemes = (i < morphemeBatches.Count ? morphemeBatches[i] : new List<WordInfo>())
                                .Where(m => m.PartOfSpeech != PartOfSpeech.BlankSpace && !string.IsNullOrWhiteSpace(m.Text))
                                .ToList();

                var (components, source) = BuildComposition(data, surface, wid, ridx, morphemes);
                if (components == null)
                {
                    stats[source]++;
                    continue;
                }

                stats[source]++;
                for (short pos = 0; pos < components.Count; pos++)
                {
                    var c = components[pos];
                    stats["componentTotal"]++;
                    if (!IsContent(data.Pos.GetValueOrDefault(c.Wid, Array.Empty<string>())))
                        stats["nameLanding"]++;
                    pending.Add(new JmDictWordComposition
                    {
                        WordId = wid, ReadingIndex = ridx, Position = pos,
                        ComponentWordId = c.Wid, ComponentReadingIndex = c.Ridx, ComponentSurface = c.Surface
                    });
                }

                if (samples.Count < 70)
                    samples.Add($"- **{surface}** [{source}] = " +
                                string.Join(" + ", components.Select(c => $"{c.Surface}({Truncate(Gloss(context, c.Wid), 22)})")));

                if (!dryRun && pending.Count >= 20_000)
                {
                    await FlushBatch(context, pending);
                    pending.Clear();
                }
            }
            if ((start / chunk) % 20 == 0)
                Console.WriteLine($"  processed {Math.Min(start + chunk, targets.Count):N0}/{targets.Count:N0}...");
        }

        if (!dryRun && pending.Count > 0) await FlushBatch(context, pending);

        int total = Math.Max(stats["total"], 1);
        int compTotal = Math.Max(stats["componentTotal"], 1);
        int kept = stats["modeA"] + stats["ruby"];
        Console.WriteLine($@"=== Composition Backfill ({(dryRun ? "DRY RUN" : "APPLIED")}) ===
Target compounds:          {stats["total"]:N0}
Kept (useful composition): {kept:N0} ({100.0 * kept / total:F1}%)  [modeA {stats["modeA"]}, ruby {stats["ruby"]}]
Dropped - phrase:          {stats["drop_phrase"]:N0}
Dropped - all single char: {stats["drop_single"]:N0}
Dropped - name-only comp:  {stats["drop_nameOnly"]:N0}
Dropped - grammatical kana:{stats["drop_grammatical"]:N0}
Dropped - infl. tail:      {stats["drop_inflTail"]:N0}
Dropped - uncovered:       {stats["drop_uncov"]:N0}
Name-only component landings: {stats["nameLanding"]:N0}/{compTotal:N0} ({100.0 * stats["nameLanding"] / compTotal:F2}%)");

        if (dryRun)
        {
            var report = "# Composition backfill dry-run samples\n\n" + string.Join("\n", samples);
            await File.WriteAllTextAsync("composition-backfill-samples.md", report, Encoding.UTF8);
            Console.WriteLine("Dry-run: wrote composition-backfill-samples.md, no rows inserted.");
        }
        else
        {
            Console.WriteLine($"Inserted {kept:N0} compositions.");
        }
    }

    /// <summary>Returns (components, source) where source is "modeA"/"ruby" on success, or a "drop_*" reason on failure.</summary>
    private static (List<(int Wid, short Ridx, string Surface)>? Components, string Source) BuildComposition(
        ResolverData data, string surface, int parentWid, short parentRidx, List<WordInfo> morphemes)
    {
        // Primary: Mode A morphemes, if it actually split the compound.
        var concat = string.Concat(morphemes.Select(m => m.Text));
        if (morphemes.Count >= 2 && concat == surface)
        {
            if (morphemes.Any(m => m.PartOfSpeech is PartOfSpeech.Particle or PartOfSpeech.Auxiliary or PartOfSpeech.Conjunction))
                return (null, "drop_phrase");
            if (morphemes.All(m => m.Text.Length == 1))
                return (null, "drop_single");

            var resolved = new List<(int, short, string)>();
            foreach (var m in morphemes)
            {
                var r = Resolve(data, m.Text, m.DictionaryForm, Hira(m.Reading), m.PartOfSpeech);
                if (r == null) return (null, "drop_uncov");
                var tags = data.Pos.GetValueOrDefault(r.Value.Wid, Array.Empty<string>());
                if (!IsContent(tags))
                    return (null, "drop_nameOnly"); // never emit a name component in a non-name composition
                // A single-hiragana component that isn't a real affix is an inflectional fragment (し, る...).
                if (m.Text.Length == 1 && JapaneseTextHelper.IsHiragana(m.Text[0]) && !IsAffix(tags))
                    return (null, "drop_grammatical");
                resolved.Add((r.Value.Wid, r.Value.Ridx, m.Text));
            }
            // An inflecting parent (verb / i-adjective) must end in a verb/adjective head; otherwise we have
            // split off its inflectional tail onto a homographic noun (可憐しい -> 可憐 + 尿).
            var parentTags = data.Pos.GetValueOrDefault(parentWid, Array.Empty<string>());
            if (IsVerbOrAdj(parentTags) && !IsVerbOrAdj(data.Pos.GetValueOrDefault(resolved[^1].Item1, Array.Empty<string>())))
                return (null, "drop_inflTail");
            return (resolved, "modeA");
        }

        // Fallback: per-kanji ruby split, but ONLY when Mode A returned the compound as a single
        // NOUN-like token. An atomic verb/adjective (溢れる, 美しい) is one lexeme — splitting its
        // okurigana into a stem + inflectional kana produces garbage components.
        bool atomicNounLike = morphemes.Count <= 1 &&
            (morphemes.Count == 0 || morphemes[0].PartOfSpeech is PartOfSpeech.Noun or PartOfSpeech.CommonNoun
                or PartOfSpeech.Unknown or PartOfSpeech.Suffix or PartOfSpeech.NounSuffix or PartOfSpeech.Prefix);
        if (atomicNounLike && data.KanjiForm.TryGetValue((parentWid, parentRidx), out var kf))
        {
            var spans = ParseRuby(kf.Text, kf.Ruby);
            var groups = spans != null ? RubyDecompose(data, spans) : null;
            // Reject if any component is a single kana (an inflectional ending, not a real component).
            if (groups is { Count: >= 2 } && !groups.All(g => g.Surface.Length == 1)
                && !groups.Any(g => g.Surface.Length == 1 && JapaneseTextHelper.IsKana(g.Surface[0])))
            {
                var resolved = new List<(int, short, string)>();
                foreach (var g in groups)
                {
                    var r = Resolve(data, g.Surface, g.Surface, g.Reading, PartOfSpeech.Unknown);
                    if (r == null) return (null, "drop_uncov");
                    if (!IsContent(data.Pos.GetValueOrDefault(r.Value.Wid, Array.Empty<string>())))
                        return (null, "drop_nameOnly");
                    resolved.Add((r.Value.Wid, r.Value.Ridx, g.Surface));
                }
                return (resolved, "ruby");
            }
        }

        return (null, "drop_uncov");
    }

    private static (int Wid, short Ridx)? Resolve(ResolverData data, string surface, string dictForm, string reading, PartOfSpeech pos)
    {
        var cands = new List<(int Wid, short Ridx)>();
        foreach (var t in new[] { surface, dictForm })
        {
            if (string.IsNullOrEmpty(t)) continue;
            if (data.KanjiByText.TryGetValue(t, out var k)) cands.AddRange(k);
            if (data.KanaByText.TryGetValue(t, out var n)) cands.AddRange(n);
        }
        if (cands.Count == 0 && !HasKanji(surface) && data.KanaByText.TryGetValue(reading, out var byReading))
            cands.AddRange(byReading);
        if (cands.Count == 0) return null;

        // Keep candidates whose reading is consistent with the in-context reading (rendaku/gemination tolerant).
        List<(int Wid, short Ridx)> filtered = string.IsNullOrEmpty(reading)
            ? cands
            : cands.Where(c => data.Readings.TryGetValue(c.Wid, out var rs) && rs.Any(rd => ReadingMatches(rd, reading))).ToList();
        if (filtered.Count == 0) filtered = cands;

        // POS-compatibility as a soft requirement: if the morpheme has a known POS and any candidate
        // matches it, restrict to those (picks the right sense, e.g. a suffix over a homographic noun).
        var posCompatible = filtered.Where(c => PosMatch(pos, data.Pos.GetValueOrDefault(c.Wid, Array.Empty<string>()))).ToList();
        if (posCompatible.Count > 0) filtered = posCompatible;

        // Pick best by sense score; tie-break preferring an exact-surface (kanji) form.
        return filtered
               .OrderByDescending(c => Score(data, c.Wid, pos))
               .ThenByDescending(c => data.KanjiForm.TryGetValue((c.Wid, c.Ridx), out var f) && f.Text == surface)
               .First();
    }

    private static double Score(ResolverData data, int wid, PartOfSpeech pos)
    {
        var tags = data.Pos.GetValueOrDefault(wid, Array.Empty<string>());
        double s = Math.Log(data.Freq.GetValueOrDefault(wid) + 1.0) * 3.0;
        if (!IsContent(tags)) s -= 60.0;
        if (PosMatch(pos, tags)) s += 8.0;
        if (data.Prio.GetValueOrDefault(wid)) s += 2.0;
        return s;
    }

    private static bool IsContent(string[] pos)
    {
        foreach (var p in pos)
            if (!NamePos.Contains(p) && !MiscPos.Contains(p))
                return true;
        return false;
    }

    private static bool IsAffix(string[] pos) =>
        pos.Any(p => p is "suf" or "n-suf" or "pref" or "n-pref" or "ctr");

    private static bool IsVerbOrAdj(string[] pos) =>
        pos.Any(p => p.StartsWith('v') || p is "adj-i" or "adj-ix");

    private static bool PosMatch(PartOfSpeech pos, string[] tags)
    {
        bool Has(params string[] xs) => tags.Any(xs.Contains);
        return pos switch
        {
            PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.Pronoun or PartOfSpeech.Numeral
                => Has("n", "n-suf", "n-pref", "pn", "num", "adj-no", "vs"),
            PartOfSpeech.Verb => tags.Any(t => t.StartsWith('v')),
            PartOfSpeech.IAdjective => tags.Contains("adj-i"),
            PartOfSpeech.NaAdjective or PartOfSpeech.NominalAdjective => tags.Contains("adj-na"),
            PartOfSpeech.Adverb or PartOfSpeech.AdverbTo => Has("adv", "adv-to"),
            PartOfSpeech.Suffix or PartOfSpeech.NounSuffix => Has("suf", "n-suf", "ctr"),
            PartOfSpeech.Prefix => Has("pref", "n-pref"),
            PartOfSpeech.Counter => tags.Contains("ctr"),
            PartOfSpeech.Adnominal or PartOfSpeech.PrenounAdjectival => tags.Contains("adj-pn"),
            PartOfSpeech.Interjection => tags.Contains("int"),
            _ => false
        };
    }

    // ---- ruby fallback ----

    private static List<(string Surface, string Reading)>? ParseRuby(string text, string ruby)
    {
        if (string.IsNullOrEmpty(ruby) || ruby == text) return null;
        var spans = new List<(string, string)>();
        int i = 0, pendingStart = 0;
        while (i < ruby.Length)
        {
            if (ruby[i] != '[') { i++; continue; }

            // The kanji run is the maximal run of kanji ending just before '['.
            int runStart = i;
            while (runStart > pendingStart && JapaneseTextHelper.IsKanji(ruby[runStart - 1])) runStart--;
            // Flush any bare kana (okurigana) before the kanji run as their own spans.
            for (int j = pendingStart; j < runStart; j++) spans.Add((ruby[j].ToString(), Hira(ruby[j].ToString())));

            int close = ruby.IndexOf(']', i);
            if (close < 0) break;
            spans.Add((ruby.Substring(runStart, i - runStart), Hira(ruby.Substring(i + 1, close - i - 1))));
            i = close + 1;
            pendingStart = i;
        }
        for (int j = pendingStart; j < ruby.Length; j++) spans.Add((ruby[j].ToString(), Hira(ruby[j].ToString())));
        return spans.Count >= 2 ? spans : null;
    }

    private static List<(string Surface, string Reading)>? RubyDecompose(ResolverData data, List<(string Surface, string Reading)> spans)
    {
        int n = spans.Count;
        var best = new Dictionary<int, List<(string, string)>?>();
        List<(string, string)>? Rec(int idx)
        {
            if (idx == n) return new List<(string, string)>();
            if (best.TryGetValue(idx, out var memo)) return memo;
            List<(string, string)>? result = null;
            int hi = idx == 0 ? n - 1 : n;
            for (int k = hi; k > idx; k--)
            {
                var surf = string.Concat(spans.GetRange(idx, k - idx).Select(s => s.Surface));
                var rdg = string.Concat(spans.GetRange(idx, k - idx).Select(s => s.Reading));
                if (!HasCandidate(data, surf, rdg)) continue;
                var tail = Rec(k);
                if (tail == null) continue;
                var comps = new List<(string, string)> { (surf, rdg) };
                comps.AddRange(tail);
                if (result == null || comps.Count < result.Count) result = comps;
            }
            best[idx] = result;
            return result;
        }
        var o = Rec(0);
        return o is { Count: >= 2 } ? o : null;
    }

    private static bool HasCandidate(ResolverData data, string surface, string reading)
    {
        IEnumerable<(int Wid, short Ridx)> pool = Array.Empty<(int, short)>();
        if (data.KanjiByText.TryGetValue(surface, out var k)) pool = pool.Concat(k);
        if (data.KanaByText.TryGetValue(surface, out var n)) pool = pool.Concat(n);
        return pool.Any(c => data.Readings.TryGetValue(c.Wid, out var rs) && rs.Any(rd => ReadingMatches(rd, reading)));
    }

    // ---- reading helpers ----

    private static bool ReadingMatches(string citation, string target)
    {
        if (citation == target) return true;
        if (citation.Length > 0 && Voice.TryGetValue(citation[0], out var voiced))
            foreach (var v in voiced)
                if (v + citation[1..] == target) return true;
        if (citation.Length >= 2 && citation[..^1] + "っ" == target) return true;
        return false;
    }

    private static string Hira(string s) =>
        string.IsNullOrEmpty(s) ? "" : WanaKana.ToHiragana(s.Replace("ヮ", "わ").Replace("ゎ", "わ"));

    private static bool HasKanji(string s) => s.Any(JapaneseTextHelper.IsKanji);

    // ---- data loading ----

    private static async Task<ResolverData> LoadData(JitenDbContext context)
    {
        var data = new ResolverData();
        var conn = (NpgsqlConnection)context.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        await using (var cmd = new NpgsqlCommand("SELECT \"WordId\",\"PartsOfSpeech\",\"Priorities\" FROM jmdict.\"Words\"", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                int wid = r.GetInt32(0);
                data.Pos[wid] = r.IsDBNull(1) ? Array.Empty<string>() : r.GetFieldValue<string[]>(1);
                data.Prio[wid] = !r.IsDBNull(2) && r.GetFieldValue<string[]>(2).Length > 0;
            }

        await using (var cmd = new NpgsqlCommand("SELECT \"WordId\",\"ObservedFrequency\" FROM jmdict.\"WordFrequencies\"", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
                if (!r.IsDBNull(1)) data.Freq[r.GetInt32(0)] = r.GetDouble(1);

        await using (var cmd = new NpgsqlCommand(
                         "SELECT \"WordId\",\"ReadingIndex\",\"Text\",\"FormType\",\"RubyText\" FROM jmdict.\"WordForms\"", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                int wid = r.GetInt32(0);
                short ridx = r.GetInt16(1);
                string text = r.GetString(2);
                short ft = r.GetInt16(3);
                if (ft == 0)
                {
                    Add(data.KanjiByText, text, (wid, ridx));
                    data.KanjiForm[(wid, ridx)] = (text, r.IsDBNull(4) ? "" : r.GetString(4));
                }
                else
                {
                    var h = Hira(text);
                    Add(data.KanaByText, h, (wid, ridx));
                    if (!data.Readings.TryGetValue(wid, out var set)) data.Readings[wid] = set = new HashSet<string>();
                    set.Add(h);
                }
            }

        return data;
    }

    private static void Add(Dictionary<string, List<(int, short)>> dict, string key, (int, short) val)
    {
        if (!dict.TryGetValue(key, out var list)) dict[key] = list = new List<(int, short)>();
        list.Add(val);
    }

    private static async Task<List<(int Wid, short Ridx, string Surface)>> LoadGapTargets(
        JitenDbContext context, ResolverData data, int limit)
    {
        // For a dry-run (limit > 0) spread the sample across the whole WordId range (prime modulo)
        // so it is representative rather than the first alphabetical cluster.
        var spread = limit > 0 ? "AND wf.\"WordId\" % 97 = 13" : "";
        var sql = $@"
SELECT wf.""WordId"", wf.""ReadingIndex"", wf.""Text""
FROM jmdict.""WordForms"" wf
WHERE wf.""FormType"" = 0 AND char_length(wf.""Text"") >= 2 {spread}
  AND NOT EXISTS (SELECT 1 FROM jmdict.""WordCompositions"" wc
                  WHERE wc.""WordId"" = wf.""WordId"" AND wc.""ReadingIndex"" = wf.""ReadingIndex"")
ORDER BY wf.""WordId""" + (limit > 0 ? $"\nLIMIT {limit}" : "");

        var conn = (NpgsqlConnection)context.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        var targets = new List<(int, short, string)>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            int wid = r.GetInt32(0);
            if (data.Pos.TryGetValue(wid, out var pos) && pos.Any(NamePos.Contains)) continue; // skip name parents
            targets.Add((wid, r.GetInt16(1), r.GetString(2)));
        }
        return targets;
    }

    private static string Gloss(JitenDbContext context, int wid)
    {
        var def = context.Database.SqlQueryRaw<string>(
            "SELECT unnest(\"EnglishMeanings\") AS \"Value\" FROM jmdict.\"Definitions\" " +
            $"WHERE \"WordId\" = {wid} ORDER BY \"SenseIndex\" LIMIT 1").AsEnumerable().FirstOrDefault();
        return def ?? "?";
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    private static async Task FlushBatch(JitenDbContext context, List<JmDictWordComposition> pending)
    {
        const int sub = 5_000;
        for (int i = 0; i < pending.Count; i += sub)
        {
            context.WordCompositions.AddRange(pending.GetRange(i, Math.Min(sub, pending.Count - i)));
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }
    }
}
